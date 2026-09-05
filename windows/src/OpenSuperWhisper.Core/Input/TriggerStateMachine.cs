namespace OpenSuperWhisper.Core.Input;

/// <summary>What a press or release should cause.</summary>
public enum TriggerAction
{
    None,
    StartRecording,
    StopRecording,
}

/// <summary>
/// Decides what a trigger press means. Port of the mac app's <c>ShortcutManager</c>
/// press handling.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately pure: no hooks, no timers, no threads. Time arrives as a parameter,
/// so every branch — including the ones that depend on how long a key was held — is
/// unit-testable without touching the OS. The hook layer above it does nothing but
/// translate real key events into these calls.
/// </para>
/// <para>
/// Not thread-safe. The coordinator serialises calls onto one thread.
/// </para>
/// </remarks>
public sealed class TriggerStateMachine
{
    /// <summary>How long a press must last to count as a hold rather than a tap.</summary>
    public static readonly TimeSpan DefaultHoldThreshold = TimeSpan.FromMilliseconds(300);

    private bool _isRecording;
    private bool _pressConsumed;
    private bool _pressStartedRecording;
    private DateTime _pressDownAt;
    private DateTime? _pendingFirstTapAt;

    /// <summary>
    /// Hold the trigger to record, release to stop. A quick tap still toggles.
    /// </summary>
    public bool HoldToRecord { get; set; } = true;

    /// <summary>Require a double tap to *start*. Stopping is always a single press.</summary>
    public bool DoublePressToTrigger { get; set; }

    public TimeSpan HoldThreshold { get; set; } = DefaultHoldThreshold;

    /// <summary>
    /// How close two presses must be to count as a double tap. Defaults to the user's
    /// own system double-click speed rather than a hardcoded value.
    /// </summary>
    public TimeSpan DoubleTapWindow { get; set; } = TimeSpan.FromMilliseconds(500);

    public bool IsRecording => _isRecording;

    /// <summary>Handles the trigger going down.</summary>
    public TriggerAction OnPressDown(DateTime now)
    {
        // A double tap is only required to START. Once recording, a single press
        // stops — forcing the user to double-tap again to stop would be miserable.
        if (DoublePressToTrigger && !_isRecording)
        {
            if (_pendingFirstTapAt is { } first && now - first <= DoubleTapWindow)
            {
                _pendingFirstTapAt = null;
            }
            else
            {
                // First tap, or the previous one timed out and this becomes the first.
                _pendingFirstTapAt = now;
                _pressConsumed = false;
                return TriggerAction.None;
            }
        }

        _pressConsumed = true;
        _pressDownAt = now;

        if (!_isRecording)
        {
            _isRecording = true;
            _pressStartedRecording = true;
            return TriggerAction.StartRecording;
        }

        // Second press while recording: stop. Hold mode is irrelevant here, because
        // hold only ever applies to the press that started the recording.
        _isRecording = false;
        _pressStartedRecording = false;
        return TriggerAction.StopRecording;
    }

    /// <summary>Handles the trigger coming up.</summary>
    public TriggerAction OnPressUp(DateTime now)
    {
        // An unconsumed press is the first half of a double tap: its release means
        // nothing. Without this guard the release would stop a recording that the
        // press never started.
        if (!_pressConsumed) return TriggerAction.None;
        _pressConsumed = false;

        var startedThisPress = _pressStartedRecording;
        _pressStartedRecording = false;

        if (!HoldToRecord || !startedThisPress || !_isRecording)
        {
            return TriggerAction.None;
        }

        // Held long enough to read as a hold: release stops. A quicker tap leaves the
        // recording running, so tap-to-start / tap-to-stop still works.
        //
        // Only the press that STARTED the recording can arm this. Arming it on the
        // stopping press would fire a second stop on release.
        if (now - _pressDownAt >= HoldThreshold)
        {
            _isRecording = false;
            return TriggerAction.StopRecording;
        }

        return TriggerAction.None;
    }

    /// <summary>
    /// Tells the machine recording ended by some other route — Escape, a transcription
    /// error, or the indicator timing out.
    /// </summary>
    /// <remarks>
    /// Without this the machine would still believe it was recording, and the user's
    /// next press would be read as "stop" and do nothing visible.
    /// </remarks>
    public void NotifyRecordingStopped()
    {
        _isRecording = false;
        _pressStartedRecording = false;
        _pendingFirstTapAt = null;
    }

    /// <summary>Clears all press state, e.g. when the trigger binding changes.</summary>
    public void Reset()
    {
        _isRecording = false;
        _pressConsumed = false;
        _pressStartedRecording = false;
        _pendingFirstTapAt = null;
    }
}
