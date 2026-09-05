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

    private readonly SemaphoreSlim _pipeline = new(1, 1);
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
        if (!_pipeline.Wait(0))
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
            _pipeline.Release();
            return;
        }

        try
        {
            _recorder.Start(device.Value);
        }
        catch (Exception)
        {
            _dispatcher.Invoke(() => ShowTransient(IndicatorState.NoMicrophone));
            _triggers.NotifyRecordingStopped();
            _pipeline.Release();
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
            _pipeline.Release();
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

            _recorder.Cancel();
            HideIndicator();
            _triggers.NotifyRecordingStopped();

            if (_pipeline.CurrentCount == 0) _pipeline.Release();
        });
    }

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
        _pipeline.Dispose();
    }
}
