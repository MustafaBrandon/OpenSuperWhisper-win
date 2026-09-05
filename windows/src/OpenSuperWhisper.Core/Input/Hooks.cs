using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Input;

/// <summary>Low-level keyboard hook. Reports key-down and key-up with the virtual key.</summary>
public sealed class KeyboardHook : LowLevelHook
{
    protected override int HookType => Win32Input.WH_KEYBOARD_LL;

    protected override unsafe HookEvent? Translate(int message, IntPtr lParam)
    {
        var data = *(Win32Input.KbdLlHookStruct*)lParam;

        // Injected events are our own synthesised keystrokes (the Ctrl+V we send to
        // paste). Treating them as user input would let the app trigger itself.
        var injected = (data.flags & Win32Input.LLKHF_INJECTED) != 0;

        return new HookEvent(message, data.vkCode, injected);
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
