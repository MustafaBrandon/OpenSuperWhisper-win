using OpenSuperWhisper.Core.Indicator;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Indicator state and cancel rules. Ports the mac suite's
/// EscapeCancelConfirmationTests plus the state transitions around them.
/// </summary>
public class IndicatorStateMachineTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    // =====================================================================
    // Cancel confirmation
    // =====================================================================

    [Fact]
    public void ShortRecording_CancelsImmediately()
    {
        // Under the threshold there is little to lose, so Escape just works.
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));

        Assert.Equal(CancelOutcome.Cancel, m.HandleCancelRequest(At(3)));
    }

    [Fact]
    public void LongRecording_FirstEscapeArms_SecondCancels()
    {
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));

        Assert.Equal(CancelOutcome.AwaitConfirmation, m.HandleCancelRequest(At(15)));
        Assert.True(m.IsConfirmingCancel(At(15)));

        Assert.Equal(CancelOutcome.Cancel, m.HandleCancelRequest(At(16)));
    }

    [Fact]
    public void LongRecording_ExactlyAtThreshold_RequiresConfirmation()
    {
        // Boundary: the comparison is >=, so ten seconds exactly is already "long".
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));

        Assert.Equal(CancelOutcome.AwaitConfirmation, m.HandleCancelRequest(At(10)));
    }

    [Fact]
    public void LongRecording_JustUnderThreshold_CancelsImmediately()
    {
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));

        Assert.Equal(CancelOutcome.Cancel, m.HandleCancelRequest(At(9.9)));
    }

    [Fact]
    public void ConfirmationDisabled_LongRecordingCancelsImmediately()
    {
        var m = new IndicatorStateMachine { CancelWithoutConfirmation = true };
        m.StartRecording(At(0));

        Assert.Equal(CancelOutcome.Cancel, m.HandleCancelRequest(At(60)));
    }

    [Fact]
    public void ConfirmationExpires_NextEscapeArmsAgain()
    {
        // The armed state lapses so that walking away mid-recording does not leave the
        // next Escape primed to discard with no warning.
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));

        Assert.Equal(CancelOutcome.AwaitConfirmation, m.HandleCancelRequest(At(15)));

        // Past the 5s window.
        Assert.False(m.IsConfirmingCancel(At(21)));
        Assert.Equal(CancelOutcome.AwaitConfirmation, m.HandleCancelRequest(At(21)));
    }

    [Fact]
    public void ConfirmationJustInsideWindow_StillCancels()
    {
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));
        m.HandleCancelRequest(At(15));

        Assert.Equal(CancelOutcome.Cancel, m.HandleCancelRequest(At(19.9)));
    }

    [Fact]
    public void Decoding_CancelsImmediatelyRegardlessOfLength()
    {
        // Nothing left to protect: the audio is already captured, and the user wants
        // out of a transcription rather than out of a recording.
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));
        Assert.True(m.StartDecoding());

        Assert.Equal(CancelOutcome.Cancel, m.HandleCancelRequest(At(60)));
    }

    [Fact]
    public void StartDecoding_ClearsArmedConfirmation()
    {
        // Otherwise a confirmation armed during recording would still be live in
        // decoding, and an Escape meant to dismiss the spinner would read as the second
        // half of a discard.
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));
        m.HandleCancelRequest(At(15));
        Assert.True(m.IsConfirmingCancel(At(15)));

        m.StartDecoding();

        Assert.False(m.IsConfirmingCancel(At(16)));
    }

    // =====================================================================
    // State transitions
    // =====================================================================

    [Fact]
    public void SecondStopWhileDecoding_IsIgnored()
    {
        // A double hotkey press, or a hold-mode key-up landing after the press that
        // stopped, must not restart decoding or hide the window mid-transcription.
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));

        Assert.True(m.StartDecoding());
        Assert.False(m.StartDecoding());
        Assert.Equal(IndicatorState.Decoding, m.State);
    }

    [Fact]
    public void Connecting_CanTransitionToDecoding()
    {
        // Stopping while a Bluetooth device is still warming up is legitimate: there
        // may be a little audio already, and the user asked to stop.
        var m = new IndicatorStateMachine();
        m.SetConnecting(At(0));

        Assert.True(m.StartDecoding());
    }

    [Fact]
    public void IdleCannotDecode()
    {
        var m = new IndicatorStateMachine();

        Assert.False(m.StartDecoding());
        Assert.Equal(IndicatorState.Idle, m.State);
    }

    [Fact]
    public void BlinkingOnlyWhileRecording()
    {
        var m = new IndicatorStateMachine();
        Assert.False(m.IsBlinking);

        m.SetConnecting(At(0));
        Assert.False(m.IsBlinking);

        m.StartRecording(At(1));
        Assert.True(m.IsBlinking);

        m.StartDecoding();
        Assert.False(m.IsBlinking);
    }

    [Fact]
    public void RecordingDuration_TracksElapsed()
    {
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));

        Assert.Equal(TimeSpan.FromSeconds(7), m.RecordingDuration(At(7)));
    }

    // =====================================================================
    // Transient messages
    // =====================================================================

    [Theory]
    [InlineData(IndicatorState.Busy)]
    [InlineData(IndicatorState.NoMicrophone)]
    public void TransientMessages_AutoDismissAfterDelay(IndicatorState message)
    {
        var m = new IndicatorStateMachine();
        m.ShowTransient(message, At(0));

        Assert.Equal(message, m.State);
        Assert.False(m.ShouldAutoDismiss(At(1.9)));
        Assert.True(m.ShouldAutoDismiss(At(2.0)));
    }

    [Fact]
    public void RecordingDoesNotAutoDismiss()
    {
        // Only transient messages hide themselves; a recording ends when the user says.
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));

        Assert.False(m.ShouldAutoDismiss(At(600)));
    }

    [Fact]
    public void TransientMessage_CannotBeArbitraryState()
    {
        var m = new IndicatorStateMachine();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => m.ShowTransient(IndicatorState.Recording, At(0)));
    }

    [Fact]
    public void Reset_ReturnsToIdle()
    {
        var m = new IndicatorStateMachine();
        m.StartRecording(At(0));
        m.HandleCancelRequest(At(15));

        m.Reset();

        Assert.Equal(IndicatorState.Idle, m.State);
        Assert.False(m.IsConfirmingCancel(At(15)));
        Assert.Null(m.RecordingDuration(At(15)));
    }
}
