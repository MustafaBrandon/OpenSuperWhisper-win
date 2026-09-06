using OpenSuperWhisper.Core.Input;
using OpenSuperWhisper.Interop;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Whether a hook event is withheld from other applications.
/// </summary>
/// <remarks>
/// This rule exists because of a real failure: with the trigger passed through, a bare
/// Alt tap put the focused window into menu mode, so the paste following a dictation
/// landed in the menu bar instead of the text field. The transcript was correct and
/// SendInput reported success, which made it look like insertion was broken when the
/// trigger was at fault.
/// </remarks>
public class TriggerSuppressionTests
{
    private const uint RightControl = 0xA3;
    private const uint RightAlt = 0xA5;

    private static HookEvent Real(uint data) => new(Win32Input.WM_KEYDOWN, data, Injected: false);
    private static HookEvent Injected(uint data) => new(Win32Input.WM_KEYDOWN, data, Injected: true);

    [Fact]
    public void BoundKey_IsWithheld()
    {
        Assert.True(LowLevelHook.ShouldSuppress(Real(RightControl), RightControl));
    }

    [Fact]
    public void UnboundKey_PassesThrough()
    {
        // Everything the user did not bind must reach the desktop untouched. A
        // low-level hook that swallows indiscriminately breaks every application.
        Assert.False(LowLevelHook.ShouldSuppress(Real(RightAlt), RightControl));
    }

    [Fact]
    public void NoTriggerBound_NothingIsWithheld()
    {
        Assert.False(LowLevelHook.ShouldSuppress(Real(RightControl), suppressedData: 0));
    }

    [Fact]
    public void InjectedInput_IsNeverWithheld()
    {
        // The important one. Our own paste keystroke is injected, and if suppression
        // applied to it the Ctrl+V would never reach the target — insertion would fail
        // in exactly the way this whole mechanism exists to fix.
        Assert.False(LowLevelHook.ShouldSuppress(Injected(RightControl), RightControl));
    }

    [Fact]
    public void InjectedTriggerKey_StillReachesTheQueue()
    {
        // Suppression and delivery are separate decisions: an injected trigger key is
        // still reported to the coordinator (which drops it for its own reasons), it is
        // just not withheld from other applications.
        Assert.False(LowLevelHook.ShouldSuppress(Injected(RightControl), RightControl));
    }

    [Theory]
    [InlineData((uint)MouseButton.Middle)]
    [InlineData((uint)MouseButton.Button4)]
    [InlineData((uint)MouseButton.Button5)]
    public void BoundMouseButton_IsWithheld(uint button)
    {
        // Otherwise a bound middle button would still paste-on-click in a terminal, or
        // a bound thumb button would still navigate back in a browser.
        var evt = new HookEvent(Win32Input.WM_MBUTTONDOWN, button, Injected: false);

        Assert.True(LowLevelHook.ShouldSuppress(evt, button));
    }

    [Fact]
    public void DefaultTrigger_IsNotAlt()
    {
        // Right Alt is AltGr on many layouts, so binding it would cost those users
        // their accented characters. Right Ctrl has no standalone behaviour to lose.
        using var coordinator = new TriggerCoordinator();

        Assert.Equal(TriggerMode.ModifierKey, coordinator.Mode);
    }

    [Theory]
    [InlineData(ModifierKey.RightControl, 0xA3)]
    [InlineData(ModifierKey.LeftControl, 0xA2)]
    [InlineData(ModifierKey.RightAlt, 0xA5)]
    [InlineData(ModifierKey.LeftAlt, 0xA4)]
    [InlineData(ModifierKey.RightShift, 0xA1)]
    [InlineData(ModifierKey.LeftShift, 0xA0)]
    [InlineData(ModifierKey.LeftWin, 0x5B)]
    [InlineData(ModifierKey.RightWin, 0x5C)]
    public void ModifierKeys_MapToSideSpecificVirtualKeys(ModifierKey key, int expected)
    {
        // Side-specific codes are what make "right Ctrl only" bindable at all — the
        // low-level hook reports them, unlike the generic VK_CONTROL.
        Assert.Equal(expected, key.ToVirtualKey());
    }
}
