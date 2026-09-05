namespace OpenSuperWhisper.Core.Indicator;

/// <summary>What the floating indicator is showing.</summary>
public enum IndicatorState
{
    /// <summary>Hidden.</summary>
    Idle,

    /// <summary>Device is waking up — Bluetooth links negotiate before delivering audio.</summary>
    Connecting,

    /// <summary>Capturing.</summary>
    Recording,

    /// <summary>Transcribing.</summary>
    Decoding,

    /// <summary>A transcription is already in flight; this request was refused.</summary>
    Busy,

    /// <summary>No usable audio input.</summary>
    NoMicrophone,
}

/// <summary>Result of an Escape press.</summary>
public enum CancelOutcome
{
    /// <summary>Cancel now.</summary>
    Cancel,

    /// <summary>
    /// Confirmation armed — the user must press Escape again to actually discard.
    /// </summary>
    AwaitConfirmation,
}

/// <summary>
/// The indicator's state and its cancel rules. Port of the mac app's
/// <c>IndicatorViewModel</c>.
/// </summary>
/// <remarks>
/// Pure, with time passed in, for the same reason the trigger machine is: the rules
/// here are all about elapsed time, and they are worth testing without waiting ten
/// real seconds. The window that renders this owns none of the logic.
/// </remarks>
public sealed class IndicatorStateMachine
{
    /// <summary>
    /// Recordings longer than this require a second Escape to discard.
    /// </summary>
    /// <remarks>
    /// A long recording represents real effort. Losing it to a stray Escape — the key
    /// people habitually hit to dismiss things — would be infuriating, so past this
    /// point discarding becomes deliberate.
    /// </remarks>
    public static readonly TimeSpan CancelConfirmationThreshold = TimeSpan.FromSeconds(10);

    /// <summary>How long an armed confirmation stays live before lapsing.</summary>
    public static readonly TimeSpan CancelConfirmationWindow = TimeSpan.FromSeconds(5);

    /// <summary>How long transient messages stay up before hiding themselves.</summary>
    public static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(2);

    /// <summary>Recording pulse period.</summary>
    public static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(800);

    private DateTime? _recordingStartedAt;
    private DateTime? _confirmationArmedAt;
    private DateTime? _autoDismissAt;

    public IndicatorState State { get; private set; } = IndicatorState.Idle;

    /// <summary>Whether Escape skips confirmation entirely.</summary>
    public bool CancelWithoutConfirmation { get; set; }

    /// <summary>Whether the indicator should be pulsing.</summary>
    public bool IsBlinking => State == IndicatorState.Recording;

    public bool IsConfirmingCancel(DateTime now) => ConfirmationLive(now);

    /// <summary>How long the current recording has been running.</summary>
    public TimeSpan? RecordingDuration(DateTime now) =>
        _recordingStartedAt is { } started ? now - started : null;

    public void StartRecording(DateTime now)
    {
        State = IndicatorState.Recording;
        _recordingStartedAt = now;
        _confirmationArmedAt = null;
        _autoDismissAt = null;
    }

    /// <summary>Device is warming up; audio is not flowing yet.</summary>
    public void SetConnecting(DateTime now)
    {
        State = IndicatorState.Connecting;
        _recordingStartedAt ??= now;
        _autoDismissAt = null;
    }

    /// <summary>
    /// Moves to decoding.
    /// </summary>
    /// <returns>
    /// False when the request is ignored — which happens for a second stop arriving
    /// while decoding is already under way. Without this guard, a double hotkey press
    /// or a hold-mode key-up landing after a press would restart decoding or hide the
    /// window mid-transcription.
    /// </returns>
    public bool StartDecoding()
    {
        if (State is not (IndicatorState.Recording or IndicatorState.Connecting)) return false;

        State = IndicatorState.Decoding;
        _confirmationArmedAt = null;
        return true;
    }

    /// <summary>Shows a transient message that hides itself.</summary>
    public void ShowTransient(IndicatorState message, DateTime now)
    {
        if (message is not (IndicatorState.Busy or IndicatorState.NoMicrophone))
        {
            throw new ArgumentOutOfRangeException(nameof(message), message,
                "Only Busy and NoMicrophone auto-dismiss.");
        }

        State = message;
        _autoDismissAt = now + AutoDismissDelay;
        _recordingStartedAt = null;
        _confirmationArmedAt = null;
    }

    /// <summary>Whether a transient message has outlived its welcome.</summary>
    public bool ShouldAutoDismiss(DateTime now) =>
        _autoDismissAt is { } at && now >= at;

    /// <summary>
    /// Decides what an Escape press means.
    /// </summary>
    /// <remarks>
    /// Confirmation is required only when every condition holds: actively recording,
    /// confirmation enabled, not already armed, and past the threshold. Anything else
    /// cancels immediately — notably Escape during <see cref="IndicatorState.Decoding"/>,
    /// where there is no recording left to protect.
    /// </remarks>
    public CancelOutcome HandleCancelRequest(DateTime now)
    {
        if (State != IndicatorState.Recording) return CancelOutcome.Cancel;
        if (CancelWithoutConfirmation) return CancelOutcome.Cancel;

        // Second press inside the window: the user meant it.
        if (ConfirmationLive(now)) return CancelOutcome.Cancel;

        if (_recordingStartedAt is not { } started) return CancelOutcome.Cancel;
        if (now - started < CancelConfirmationThreshold) return CancelOutcome.Cancel;

        _confirmationArmedAt = now;
        return CancelOutcome.AwaitConfirmation;
    }

    /// <summary>Returns to hidden and clears everything.</summary>
    public void Reset()
    {
        State = IndicatorState.Idle;
        _recordingStartedAt = null;
        _confirmationArmedAt = null;
        _autoDismissAt = null;
    }

    /// <summary>
    /// An armed confirmation lapses on its own, so walking away does not leave the
    /// next Escape primed to discard without warning.
    /// </summary>
    private bool ConfirmationLive(DateTime now) =>
        _confirmationArmedAt is { } armed && now - armed < CancelConfirmationWindow;
}
