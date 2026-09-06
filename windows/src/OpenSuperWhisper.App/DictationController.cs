using System.IO;
using System.Windows.Threading;
using OpenSuperWhisper.Core.Audio;
using OpenSuperWhisper.Core.Diagnostics;
using OpenSuperWhisper.Core.Indicator;
using OpenSuperWhisper.Core.Input;
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

    public InsertionOptions Insertion { get; set; } = new();

    /// <summary>Raised with each finished transcript, for the history and the tray.</summary>
    public event EventHandler<string>? Transcribed;

    public DictationController(Dispatcher dispatcher, IndicatorWindow indicator,
        string modelPath, string vadModelPath)
    {
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
                var samples = AudioDecoder.DecodeToWhisperFormat(wav);
                var text = _engine.Transcribe(samples);

                if (string.IsNullOrEmpty(text))
                {
                    Log.Write("  no speech detected");
                    return;
                }

                Log.Write($"  transcript: {text}");

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
                try { File.Delete(wav); } catch (IOException) { }
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

    private void RenderState()
    {
        var now = DateTime.UtcNow;
        _indicator.Render(_state.State, _state.RecordingDuration(now), _state.IsConfirmingCancel(now));
    }

    private void HideIndicator()
    {
        _state.Reset();
        _indicator.Hide();
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
