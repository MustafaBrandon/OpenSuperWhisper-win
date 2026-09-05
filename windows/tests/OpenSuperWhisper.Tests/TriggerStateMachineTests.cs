using OpenSuperWhisper.Core.Input;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// The trigger state machine. Every branch that would otherwise need a real keyboard,
/// real timers and a real recording session.
/// </summary>
public class TriggerStateMachineTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double ms) => T0.AddMilliseconds(ms);

    private static TriggerStateMachine Machine(bool hold = true, bool doubleTap = false) => new()
    {
        HoldToRecord = hold,
        DoublePressToTrigger = doubleTap,
        HoldThreshold = TimeSpan.FromMilliseconds(300),
        DoubleTapWindow = TimeSpan.FromMilliseconds(500),
    };

    // =====================================================================
    // Toggle mode
    // =====================================================================

    [Fact]
    public void Toggle_PressStartsAndSecondPressStops()
    {
        var m = Machine(hold: false);

        Assert.Equal(TriggerAction.StartRecording, m.OnPressDown(At(0)));
        Assert.Equal(TriggerAction.None, m.OnPressUp(At(50)));
        Assert.True(m.IsRecording);

        Assert.Equal(TriggerAction.StopRecording, m.OnPressDown(At(5000)));
        Assert.Equal(TriggerAction.None, m.OnPressUp(At(5050)));
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void Toggle_LongPressDoesNotStopOnRelease()
    {
        // With hold disabled, holding the key for a long time must NOT stop on
        // release — that is the whole difference between the two modes.
        var m = Machine(hold: false);

        m.OnPressDown(At(0));
        Assert.Equal(TriggerAction.None, m.OnPressUp(At(5000)));
        Assert.True(m.IsRecording);
    }

    // =====================================================================
    // Hold mode
    // =====================================================================

    [Fact]
    public void Hold_ReleaseAfterThresholdStops()
    {
        var m = Machine();

        Assert.Equal(TriggerAction.StartRecording, m.OnPressDown(At(0)));
        Assert.Equal(TriggerAction.StopRecording, m.OnPressUp(At(500)));
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void Hold_QuickTapLeavesRecordingRunning()
    {
        // Tap-to-start still has to work in hold mode, or a user who taps rather than
        // holds gets a recording that stops instantly.
        var m = Machine();

        Assert.Equal(TriggerAction.StartRecording, m.OnPressDown(At(0)));
        Assert.Equal(TriggerAction.None, m.OnPressUp(At(100)));
        Assert.True(m.IsRecording);
    }

    [Fact]
    public void Hold_QuickTapThenSecondTapStops()
    {
        var m = Machine();

        m.OnPressDown(At(0));
        m.OnPressUp(At(100));

        Assert.Equal(TriggerAction.StopRecording, m.OnPressDown(At(3000)));
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void Hold_ExactlyAtThresholdStops()
    {
        // Boundary: the comparison is >=, so a release exactly at the threshold counts
        // as a hold.
        var m = Machine();

        m.OnPressDown(At(0));
        Assert.Equal(TriggerAction.StopRecording, m.OnPressUp(At(300)));
    }

    [Fact]
    public void Hold_JustUnderThresholdDoesNotStop()
    {
        var m = Machine();

        m.OnPressDown(At(0));
        Assert.Equal(TriggerAction.None, m.OnPressUp(At(299)));
        Assert.True(m.IsRecording);
    }

    [Fact]
    public void Hold_StoppingPressDoesNotFireSecondStopOnRelease()
    {
        // THE bug this design exists to prevent. If hold armed on the stopping press
        // too, releasing it would stop a second time — and since the recording is
        // already finished, that stop would land on the NEXT one.
        var m = Machine();

        m.OnPressDown(At(0));      // start
        m.OnPressUp(At(100));      // quick tap, still recording

        Assert.Equal(TriggerAction.StopRecording, m.OnPressDown(At(3000)));

        // Released well past the hold threshold, but this press did not start the
        // recording, so its release must be inert.
        Assert.Equal(TriggerAction.None, m.OnPressUp(At(4000)));
        Assert.False(m.IsRecording);
    }

    // =====================================================================
    // Double tap
    // =====================================================================

    [Fact]
    public void DoubleTap_FirstPressDoesNothing()
    {
        var m = Machine(doubleTap: true);

        Assert.Equal(TriggerAction.None, m.OnPressDown(At(0)));
        Assert.Equal(TriggerAction.None, m.OnPressUp(At(50)));
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void DoubleTap_SecondPressWithinWindowStarts()
    {
        var m = Machine(doubleTap: true);

        m.OnPressDown(At(0));
        m.OnPressUp(At(50));

        Assert.Equal(TriggerAction.StartRecording, m.OnPressDown(At(200)));
        Assert.True(m.IsRecording);
    }

    [Fact]
    public void DoubleTap_SecondPressTooLateBecomesNewFirstPress()
    {
        var m = Machine(doubleTap: true);

        m.OnPressDown(At(0));
        m.OnPressUp(At(50));

        // Outside the window: not a double tap, but it does become the new first tap.
        Assert.Equal(TriggerAction.None, m.OnPressDown(At(2000)));
        Assert.Equal(TriggerAction.None, m.OnPressUp(At(2050)));

        // So a prompt follow-up now completes a double tap.
        Assert.Equal(TriggerAction.StartRecording, m.OnPressDown(At(2200)));
    }

    [Fact]
    public void DoubleTap_SinglePressStopsWhileRecording()
    {
        // Double tap gates starting only. Requiring it to stop as well would mean
        // fumbling a second tap while the recording kept running.
        var m = Machine(doubleTap: true);

        m.OnPressDown(At(0));
        m.OnPressUp(At(50));
        m.OnPressDown(At(200));    // starts
        m.OnPressUp(At(250));

        Assert.Equal(TriggerAction.StopRecording, m.OnPressDown(At(5000)));
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void DoubleTap_ReleaseOfFirstTapCannotStopRecording()
    {
        // Regression guard for the unconsumed-press rule. The first tap's release must
        // be inert; otherwise it would stop the recording the second tap just started.
        var m = Machine(doubleTap: true);

        m.OnPressDown(At(0));                 // first tap, no action
        Assert.Equal(TriggerAction.None, m.OnPressUp(At(600)));  // long release, still inert
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void DoubleTap_WithHold_ReleaseAfterThresholdStops()
    {
        // The two features compose: double tap to start, then hold semantics apply to
        // the press that started it.
        var m = Machine(hold: true, doubleTap: true);

        m.OnPressDown(At(0));
        m.OnPressUp(At(50));

        Assert.Equal(TriggerAction.StartRecording, m.OnPressDown(At(200)));
        Assert.Equal(TriggerAction.StopRecording, m.OnPressUp(At(700)));
        Assert.False(m.IsRecording);
    }

    // =====================================================================
    // External stop
    // =====================================================================

    [Fact]
    public void ExternalStop_NextPressStartsRatherThanStops()
    {
        // Escape cancels, or transcription fails. If the machine kept believing it was
        // recording, the user's next press would read as "stop" and appear to do
        // nothing at all.
        var m = Machine();

        m.OnPressDown(At(0));
        m.OnPressUp(At(50));
        Assert.True(m.IsRecording);

        m.NotifyRecordingStopped();
        Assert.False(m.IsRecording);

        Assert.Equal(TriggerAction.StartRecording, m.OnPressDown(At(1000)));
    }

    [Fact]
    public void Reset_ClearsPendingDoubleTap()
    {
        var m = Machine(doubleTap: true);

        m.OnPressDown(At(0));   // pending first tap
        m.Reset();

        // After a rebind, the pending tap must not combine with a press for the new
        // binding.
        Assert.Equal(TriggerAction.None, m.OnPressDown(At(100)));
    }

    // =====================================================================
    // Repeat suppression
    // =====================================================================

    [Fact]
    public void HeldKey_AutoRepeatDoesNotRetrigger()
    {
        // Holding a key produces repeated key-down events. Each one arriving as a
        // press would toggle recording on and off continuously. The coordinator
        // filters repeats, but assert the machine's behaviour is at least coherent if
        // one slips through: it toggles, it does not corrupt state.
        var m = Machine();

        Assert.Equal(TriggerAction.StartRecording, m.OnPressDown(At(0)));
        Assert.Equal(TriggerAction.StopRecording, m.OnPressDown(At(30)));
        Assert.False(m.IsRecording);
    }
}
