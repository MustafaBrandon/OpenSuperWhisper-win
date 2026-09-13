using System.IO;
using System.Windows.Threading;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Audio;
using OpenSuperWhisper.Core.Diagnostics;
using OpenSuperWhisper.Core.History;
using OpenSuperWhisper.Core.Indicator;
using OpenSuperWhisper.Core.Input;
using OpenSuperWhisper.Core.Settings;
using OpenSuperWhisper.Core.Text;
using OpenSuperWhisper.Core.Transcription;

namespace OpenSuperWhisper.App;

/// <summary>
/// Drives the whole dictation lifecycle: trigger, capture, transcribe, insert, and the
/// indicator that reflects it.
/// </summary>
/// <remarks>
/// Port of the mac app's <c>IndicatorViewModel.startDecoding</c> flow. All state lives
/// in <see cref="IndicatorStateMachine"/>; this class owns the I/O and the threading.
/// </remarks>
public sealed class DictationController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IndicatorWindow _indicator;
    private readonly IndicatorStateMachine _state = new();
    private readonly CaretLocator _caret;
    private readonly MicrophoneService _microphones;
    private readonly AudioRecorder _recorder = new();
    private readonly TriggerCoordinator _triggers = new();
    private readonly WhisperEngine _engine;
    private readonly RecordingStore? _history;
    private readonly DispatcherTimer _tick;

    /// <summary>
    /// 1 while a dictation session owns the pipeline, 0 when idle.
    /// </summary>
    /// <remarks>
    /// Replaces a semaphore that was released from several places, guarded by a
    /// <c>CurrentCount == 0</c> check. That check was a race, and Escape during
    /// decoding hit it squarely: the cancel path released while the transcription
    /// task was still running and would release again in its finally, which either
    /// throws or over-releases and later permits two concurrent recordings.
    /// <para>
    /// Interlocked claim and release makes ownership unambiguous and ending a session
    /// idempotent — whoever gets there first wins, everyone else is a no-op.
    /// </para>
    /// </remarks>
    private int _sessionActive;

    /// <summary>
    /// Set when the user cancels a session that has already reached transcription.
    /// </summary>
    /// <remarks>
    /// Escape during decoding cannot un-run whisper, but it must stop the result being
    /// inserted. Without this the indicator disappeared and the transcript was pasted
    /// anyway, which reads as the cancel having been ignored.
    /// </remarks>
    private int _sessionCancelled;

    private bool _disposed;

    private AppSettings _settings = new();

    /// <summary>
    /// Applies user settings to every layer that consumes them.
    /// </summary>
    /// <remarks>
    /// Called at startup and whenever settings change, so an edit takes effect without
    /// a restart. Trigger rebinding tears down and reinstalls the hooks, which is why
    /// it only happens when the binding actually changed.
    /// </remarks>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var previous = _settings;
        _settings = settings;

        Log.Verbose = settings.DebugMode;

        _triggers.HoldToRecord = settings.HoldToRecord;
        _triggers.DoublePressToTrigger = settings.DoublePressToTrigger;

        if (settings.SelectedMicrophoneId != previous.SelectedMicrophoneId
            || _microphones.PreferredDeviceId != settings.SelectedMicrophoneId)
        {
            _microphones.SetPreferredDevice(settings.SelectedMicrophoneId);
        }

        // Rebinding reinstalls both hooks, so avoid it unless the binding moved.
        var bindingChanged = previous.ModifierOnlyHotkey != settings.ModifierOnlyHotkey
            || previous.MouseButtonHotkey != settings.MouseButtonHotkey
            || previous.ShortcutHotkey != settings.ShortcutHotkey;

        if (bindingChanged) ApplyTriggerBinding(settings);

        Log.Write($"settings applied: trigger={settings.ModifierOnlyHotkey}/{settings.MouseButtonHotkey}, "
                + $"hold={settings.HoldToRecord}, paste={settings.AutoPasteTranscription}, "
                + $"language={settings.WhisperLanguage}");

        Log.Detail(() =>
            $"  decoding: beam={settings.UseBeamSearch}/{settings.BeamSize}, "
            + $"temperature={settings.Temperature}, noSpeech={settings.NoSpeechThreshold}, "
            + $"suppressBlank={settings.SuppressBlankAudio}, timestamps={settings.ShowTimestamps}, "
            + $"prompt={(settings.InitialPrompt.Length == 0 ? "(none)" : $"{settings.InitialPrompt.Length} chars")}"
            + $"; output: copy={settings.AutoCopyToClipboard}, unicode={settings.UseUnicodeTyping}, "
            + $"trailingSpace={settings.AddSpaceAfterSentence}"
            + $"; history={settings.SaveTranscriptionHistory}, doubleTap={settings.DoublePressToTrigger}, "
            + $"escNoConfirm={settings.EscCancelWithoutConfirmation}, sound={settings.PlaySoundOnRecordStart}");
    }

    /// <summary>Binds whichever trigger the settings resolve to.</summary>
    /// <remarks>
    /// The precedence between the three modes lives in
    /// <see cref="TriggerBindings.Resolve"/>, where it can be tested without a keyboard.
    /// </remarks>
    public void ApplyTriggerBinding(AppSettings settings)
    {
        var resolved = TriggerBindings.Resolve(settings);

        switch (resolved.Mode)
        {
            case TriggerMode.MouseButton:
                _triggers.UseMouseButton(resolved.Button);
                break;

            case TriggerMode.Shortcut:
                _triggers.UseShortcut(resolved.Shortcut);
                break;

            default:
                _triggers.UseModifierKey(resolved.Key);
                break;
        }
    }

    private InsertionOptions Insertion => new(
        Paste: _settings.AutoPasteTranscription,
        CopyToClipboard: _settings.AutoCopyToClipboard,
        Method: _settings.UseUnicodeTyping
            ? InsertionMethod.UnicodeTyping
            : InsertionMethod.ClipboardPaste,
        AddTrailingSpace: _settings.AddSpaceAfterSentence);

    private TranscriptionSettings Transcription => new()
    {
        Language = _settings.WhisperLanguage,
        SuppressBlankAudio = _settings.SuppressBlankAudio,
        ShowTimestamps = _settings.ShowTimestamps,
        Temperature = (float)_settings.Temperature,
        NoSpeechThreshold = (float)_settings.NoSpeechThreshold,
        InitialPrompt = _settings.InitialPrompt,
        UseBeamSearch = _settings.UseBeamSearch,
        BeamSize = _settings.BeamSize,
    };

    /// <summary>Raised with each finished transcript, for the history and the tray.</summary>
    public event EventHandler<string>? Transcribed;

    public DictationController(Dispatcher dispatcher, IndicatorWindow indicator,
        string modelPath, string vadModelPath, RecordingStore? history = null)
    {
        _history = history;
        _dispatcher = dispatcher;
        _indicator = indicator;
        _microphones = new MicrophoneService();
        _engine = new WhisperEngine(modelPath, vadModelPath);

        // Cheapest first, and the mouse fallback is inside CaretLocator itself.
        _caret = new CaretLocator([new Win32CaretStrategy(), new UiaCaretStrategy()]);

        _triggers.StartRequested += (_, _) => OnStartRequested();
        _triggers.StopRequested += (_, _) => OnStopRequested();
        _triggers.CancelRequested += (_, _) => OnCancelRequested();

        // Drives the elapsed counter, the auto-dismiss of transient messages, and the
        // lapse of an armed cancel confirmation. One timer rather than three.
        _tick = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _tick.Tick += (_, _) => OnTick();
    }

    public TriggerCoordinator Triggers => _triggers;
    public MicrophoneService Microphones => _microphones;

    public void Start()
    {
        _triggers.Start();
        _tick.Start();
    }

    private void OnStartRequested()
    {
        Log.Write("trigger: start requested");

        // Refuse rather than queue: a second recording while the first is still
        // transcribing would contend for the engine, and telling the user is better
        // than silently stacking work.
        if (!TryBeginSession())
        {
            Log.Write("  refused: pipeline busy");
            _dispatcher.Invoke(() => ShowTransient(IndicatorState.Busy));
            _triggers.NotifyRecordingStopped();
            return;
        }

        var device = _microphones.ActiveDevice;
        if (device is null)
        {
            _dispatcher.Invoke(() => ShowTransient(IndicatorState.NoMicrophone));
            _triggers.NotifyRecordingStopped();
            EndSession();
            return;
        }

        try
        {
            _recorder.Start(device.Value);
            PlayStartSound();
        }
        catch (Exception ex)
        {
            Log.Error("could not start capture", ex);
            _dispatcher.Invoke(() => ShowTransient(IndicatorState.NoMicrophone));
            _triggers.NotifyRecordingStopped();
            EndSession();
            return;
        }

        _dispatcher.Invoke(() =>
        {
            // Show the indicator before resolving the anchor. Anchor resolution talks to
            // another process and is budgeted, but even 150 ms of delay before any
            // feedback would feel like the hotkey had not registered.
            _state.StartRecording(DateTime.UtcNow);

            if (device.Value.RequiresWarmUp) _state.SetConnecting(DateTime.UtcNow);

            RenderState();
            _indicator.Show();

            var anchor = _caret.Resolve();
            _indicator.MoveToAnchor(anchor);

            Log.Write($"  recording; indicator at {anchor.X},{anchor.Y} via {anchor.Source} " +
                      $"(visible={_indicator.IsVisible} {_indicator.Left:F0},{_indicator.Top:F0} " +
                      $"{_indicator.ActualWidth:F0}x{_indicator.ActualHeight:F0})");
        });
    }

    /// <summary>
    /// Confirms the microphone is live, when the user asked for a sound.
    /// </summary>
    /// <remarks>
    /// Played after capture starts, not before: the point of the sound is "I am
    /// recording now", and sounding it ahead of a device that then fails to open would
    /// be a lie at exactly the moment the user is deciding whether to start talking.
    /// <para>
    /// A system sound rather than a bundled asset. It plays asynchronously — so it
    /// costs the trigger path nothing — and it respects the user's sound scheme,
    /// including having muted it, which a raw waveform would not.
    /// </para>
    /// </remarks>
    private void PlayStartSound()
    {
        if (!_settings.PlaySoundOnRecordStart) return;

        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch (Exception ex)
        {
            // No audio endpoint, or a sound scheme that cannot be read. Never worth
            // failing a dictation over.
            Log.Error("could not play the start sound", ex);
        }
    }

    private void OnStopRequested()
    {
        var accepted = false;
        _dispatcher.Invoke(() =>
        {
            accepted = _state.StartDecoding();
            if (accepted) RenderState();
        });

        // A second stop while already decoding: ignore it rather than re-entering.
        if (!accepted)
        {
            Log.Write("trigger: stop ignored (not recording)");
            return;
        }

        Log.Write("trigger: stop requested, transcribing");
        _ = Task.Run(RunTranscriptionAsync);
    }

    private async Task RunTranscriptionAsync()
    {
        try
        {
            var wav = await _recorder.StopAsync().ConfigureAwait(false);
            if (wav is null)
            {
                Log.Write("  discarded: under minimum duration");
                return;
            }

            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();

                var samples = AudioDecoder.DecodeToWhisperFormat(wav);
                var audioSeconds = samples.Length / (double)AudioDecoder.WhisperSampleRate;
                var decoded = started.Elapsed;

                var text = _engine.Transcribe(samples, Transcription);

                Log.Detail(() =>
                    $"  audio {audioSeconds:F2}s ({samples.Length} samples), "
                    + $"decode {decoded.TotalMilliseconds:F0}ms, "
                    + $"transcribe {(started.Elapsed - decoded).TotalMilliseconds:F0}ms "
                    + $"({audioSeconds / Math.Max(0.001, (started.Elapsed - decoded).TotalSeconds):F1}x realtime)");

                if (string.IsNullOrEmpty(text))
                {
                    Log.Write("  no speech detected");
                    return;
                }

                Log.Write($"  transcript: {text}");

                // Store before inserting: if insertion fails the transcript is still
                // recoverable from history, which is the whole point of keeping it.
                StoreIfEnabled(text, wav);

                // Escape cannot un-run whisper, but it must stop the result landing in
                // the user's document. Checked here rather than earlier because the
                // cancel can arrive at any point during the decode.
                if (Volatile.Read(ref _sessionCancelled) == 1)
                {
                    Log.Write("  discarded: cancelled during transcription");
                    return;
                }

                _dispatcher.Invoke(() =>
                {
                    var outcome = TextInjector.Insert(text, Insertion);
                    Log.Write($"  insertion: {outcome}");

                    if (outcome == InsertionResult.BlockedByElevation)
                    {
                        // Falling back to the clipboard means the work is not lost even
                        // though the paste could not land.
                        ClipboardService.SetText(text);
                    }
                });

                Transcribed?.Invoke(this, text);
            }
            finally
            {
                if (File.Exists(wav)) { try { File.Delete(wav); } catch (IOException) { } }
            }
        }
        catch (Exception ex)
        {
            // A failed transcription must still tear the session down cleanly, or the
            // next hotkey press finds the pipeline permanently held.
            Log.Error("transcription failed", ex);
        }
        finally
        {
            _dispatcher.Invoke(HideIndicator);
            _triggers.NotifyRecordingStopped();
            EndSession();
        }
    }

    private void OnCancelRequested()
    {
        _dispatcher.Invoke(() =>
        {
            if (_state.HandleCancelRequest(DateTime.UtcNow) == CancelOutcome.AwaitConfirmation)
            {
                // Armed, not cancelled: the card now says a second press will discard.
                RenderState();
                return;
            }

            Log.Write("trigger: cancelled");

            // Flag first. If a transcription is already in flight it owns the session
            // and will end it; this only tells it to throw the result away.
            Volatile.Write(ref _sessionCancelled, 1);

            _recorder.Cancel();
            HideIndicator();
            _triggers.NotifyRecordingStopped();

            // Idempotent: a no-op when the transcription task still owns the session.
            EndSession();
        });
    }

    /// <summary>
    /// Records a transcript and its audio, when history is enabled.
    /// </summary>
    /// <remarks>
    /// With history off, nothing is written at all — no row, and the audio is left for
    /// the caller to delete. "Off" has to mean not recorded, not recorded-and-hidden,
    /// or the setting is worthless to anyone who turns it off for privacy.
    /// <para>
    /// The audio is <i>moved</i> rather than copied: it is our own temp capture, and
    /// duplicating a recording to leave a copy in the temp directory would be wasteful
    /// and would defeat the sweeper.
    /// </para>
    /// </remarks>
    private void StoreIfEnabled(string transcript, string wavPath)
    {
        if (!_settings.SaveTranscriptionHistory) return;
        if (_history is null) return;

        try
        {
            var duration = 0.0;
            try
            {
                duration = AudioDecoder.DecodeToWhisperFormat(wavPath).Length
                    / (double)AudioDecoder.WhisperSampleRate;
            }
            catch (Exception)
            {
                // A missing duration is cosmetic; it must not cost the transcript.
            }

            var recording = Recording.ForDictation(transcript, duration);

            Directory.CreateDirectory(AppPaths.Recordings);
            File.Move(wavPath, recording.AudioPath, overwrite: true);

            _history.Add(recording);
            Log.Write($"  stored as {recording.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error("could not store the recording", ex);
        }
    }

    /// <summary>
    /// Transcribes an audio file dropped by the user.
    /// </summary>
    /// <remarks>
    /// Shares the session flag with dictation, so a dropped file cannot decode while a
    /// recording is transcribing — one whisper context cannot serve both, and letting
    /// them overlap would corrupt each other's decoding state.
    /// <para>
    /// The file is only read. Unlike our own captures it is never moved or deleted:
    /// it belongs to the user and stays where they put it.
    /// </para>
    /// </remarks>
    public async Task<string> TranscribeFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found.", path);

        // Wait rather than refuse: the user explicitly asked for this file, and a
        // silent "busy" would look like the drop did nothing.
        while (!TryBeginSession()) await Task.Delay(200).ConfigureAwait(false);

        try
        {
            return await Task.Run(() =>
            {
                var samples = AudioDecoder.DecodeToWhisperFormat(path);
                return _engine.Transcribe(samples, Transcription);
            }).ConfigureAwait(false);
        }
        finally
        {
            EndSession();
        }
    }

    /// <summary>Claims the pipeline for a new dictation, if it is free.</summary>
    private bool TryBeginSession()
    {
        if (Interlocked.CompareExchange(ref _sessionActive, 1, 0) != 0) return false;

        Volatile.Write(ref _sessionCancelled, 0);
        return true;
    }

    /// <summary>Releases the pipeline. Safe to call more than once.</summary>
    private void EndSession() => Interlocked.Exchange(ref _sessionActive, 0);

    private void OnTick()
    {
        if (_state.State == IndicatorState.Idle) return;

        if (_state.ShouldAutoDismiss(DateTime.UtcNow))
        {
            HideIndicator();
            return;
        }

        RenderState();
    }

    private void ShowTransient(IndicatorState message)
    {
        _state.ShowTransient(message, DateTime.UtcNow);
        RenderState();
        _indicator.Show();
        _indicator.MoveToAnchor(_caret.Resolve());
    }

    /// <summary>
    /// Raised on the UI thread whenever the indicator state changes, so the shell can
    /// mirror it — the tray icon shows whether the microphone is open.
    /// </summary>
    public event EventHandler<IndicatorState>? StateChanged;

    private void RenderState()
    {
        var now = DateTime.UtcNow;
        _indicator.Render(_state.State, _state.RecordingDuration(now), _state.IsConfirmingCancel(now));

        StateChanged?.Invoke(this, _state.State);
    }

    private void HideIndicator()
    {
        _state.Reset();
        _indicator.Hide();

        StateChanged?.Invoke(this, IndicatorState.Idle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tick.Stop();
        _triggers.Dispose();
        _recorder.Dispose();
        _microphones.Dispose();
        _engine.Dispose();
    }
}
