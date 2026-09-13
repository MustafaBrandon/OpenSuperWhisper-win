using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Input;

/// <summary>Low-level keyboard hook. Reports key-down and key-up with the virtual key.</summary>
public sealed class KeyboardHook : LowLevelHook
{
    protected override int HookType => Win32Input.WH_KEYBOARD_LL;

    /// <summary>
    /// Modifiers that must also be held before <see cref="LowLevelHook.SuppressedData"/>
    /// is withheld.
    /// </summary>
    /// <remarks>
    /// Set for a shortcut trigger, so that binding Alt+` withholds the backtick only
    /// while Alt is down. Without this the user would lose the key outright — a
    /// backtick that types nothing whenever the app is running is a far bigger loss
    /// than the shortcut is a gain.
    /// </remarks>
    public volatile ShortcutModifiers RequiredModifiers = ShortcutModifiers.None;

    protected override unsafe HookEvent? Translate(int message, IntPtr lParam)
    {
        var data = *(Win32Input.KbdLlHookStruct*)lParam;

        // Injected events are our own synthesised keystrokes (the Ctrl+V we send to
        // paste). Treating them as user input would let the app trigger itself.
        var injected = (data.flags & Win32Input.LLKHF_INJECTED) != 0;

        return new HookEvent(message, data.vkCode, injected, CurrentModifiers());
    }

    /// <summary>
    /// Whether the key is the bound one <i>and</i> the required modifiers are held.
    /// </summary>
    /// <remarks>
    /// Key-up is matched on the key alone. Requiring the modifiers on release would
    /// strand a recording whenever the user let go of Alt a moment before the main
    /// key — which is most of the time, and would leave the app recording with no
    /// visible way to stop.
    /// </remarks>
    protected override bool SuppressMatches(in HookEvent evt) =>
        ShouldSuppressWithModifiers(evt, SuppressedData, RequiredModifiers);

    /// <summary>
    /// The rule itself, pure so it can be tested without installing a hook.
    /// </summary>
    /// <remarks>
    /// With no required modifiers this is exactly the bare-key rule: withhold the bound
    /// key whenever it appears.
    /// </remarks>
    internal static bool ShouldSuppressWithModifiers(
        in HookEvent evt, uint suppressedData, ShortcutModifiers required)
    {
        if (!ShouldSuppress(evt, suppressedData)) return false;

        if (required == ShortcutModifiers.None) return true;

        var isUp = evt.Message is Win32Input.WM_KEYUP or Win32Input.WM_SYSKEYUP;

        return isUp || evt.Modifiers == required;
    }

    /// <summary>
    /// Snapshot of the modifiers currently held.
    /// </summary>
    /// <remarks>
    /// Side-agnostic: either Ctrl satisfies Ctrl. Reads the async key state rather than
    /// tracking the hook's own events, which would be wrong for any modifier that was
    /// already down when the hook was installed.
    /// </remarks>
    internal static ShortcutModifiers CurrentModifiers()
    {
        var modifiers = ShortcutModifiers.None;

        if (IsDown(Win32Input.VK_CONTROL)) modifiers |= ShortcutModifiers.Control;
        if (IsDown(Win32Input.VK_MENU)) modifiers |= ShortcutModifiers.Alt;
        if (IsDown(Win32Input.VK_SHIFT)) modifiers |= ShortcutModifiers.Shift;

        // No generic VK for Windows keys — they have to be asked for by side.
        if (IsDown(Win32Input.VK_LWIN) || IsDown(Win32Input.VK_RWIN))
        {
            modifiers |= ShortcutModifiers.Win;
        }

        return modifiers;

        // The high bit means held. The low bit ("pressed since last call") is
        // deliberately ignored: it is consumed by whoever reads it first, which makes
        // it useless to a hook that shares the desktop with other readers.
        static bool IsDown(int virtualKey) => (Win32Input.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }
}

/// <summary>Low-level mouse hook. Reports only the buttons that can be bound.</summary>
public sealed class MouseHook : LowLevelHook
{
    protected override int HookType => Win32Input.WH_MOUSE_LL;

    protected override unsafe HookEvent? Translate(int message, IntPtr lParam)
    {
        // Ignore everything except the bindable buttons. Mouse movement floods this
        // callback, and the latency budget does not allow processing it.
        if (message is not (Win32Input.WM_MBUTTONDOWN or Win32Input.WM_MBUTTONUP
            or Win32Input.WM_XBUTTONDOWN or Win32Input.WM_XBUTTONUP))
        {
            return null;
        }

        var data = *(Win32Input.MsllHookStruct*)lParam;

        uint button;
        if (message is Win32Input.WM_MBUTTONDOWN or Win32Input.WM_MBUTTONUP)
        {
            button = (uint)MouseButton.Middle;
        }
        else
        {
            // For X buttons the identity is in the high word of mouseData.
            var xButton = (data.mouseData >> 16) & 0xFFFF;
            button = xButton switch
            {
                Win32Input.XBUTTON1 => (uint)MouseButton.Button4,
                Win32Input.XBUTTON2 => (uint)MouseButton.Button5,
                _ => 0,
            };

            if (button == 0) return null;
        }

        var injected = (data.flags & 0x01) != 0;   // LLMHF_INJECTED
        return new HookEvent(message, button, injected);
    }
}
