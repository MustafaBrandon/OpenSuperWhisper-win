using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Input;

/// <summary>
/// Key-cap labels for the active keyboard layout. Port of the mac app's
/// <c>KeyboardLayoutProvider</c>.
/// </summary>
/// <remarks>
/// A shortcut recorder that shows the wrong label is worse than one that shows none:
/// the user is told to press a key that is not where the app says it is. The virtual
/// key <c>0xC0</c> is <c>`</c> on a US layout, <c>^</c> on German, <c>²</c> on French
/// and <c>ё</c> on Russian — same key, same code, four labels. Windows knows which,
/// via the scan code, so ask it rather than shipping a table of guesses.
/// </remarks>
public static class KeyboardLayoutProvider
{
    /// <summary>
    /// Names for keys whose <c>GetKeyNameText</c> label is unhelpful or empty.
    /// </summary>
    /// <remarks>
    /// These are the layout-independent keys — their position and meaning are the same
    /// everywhere — so a fixed name is correct, and in several cases better than what
    /// Windows returns. Everything layout-dependent is deliberately absent: those must
    /// come from the layout itself.
    /// </remarks>
    private static readonly Dictionary<int, string> FixedNames = new()
    {
        [0x08] = "Backspace",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x1B] = "Esc",
        [0x20] = "Space",
        [0x21] = "Page Up",
        [0x22] = "Page Down",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "Left",
        [0x26] = "Up",
        [0x27] = "Right",
        [0x28] = "Down",
        [0x2C] = "Print Screen",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0x90] = "Num Lock",
        [0x91] = "Scroll Lock",
        [0x13] = "Pause",
    };

    /// <summary>The layout of the calling thread.</summary>
    public static IntPtr ActiveLayout() => Win32Window.GetKeyboardLayout(0);

    /// <summary>
    /// The label printed on the key that produces this virtual key.
    /// </summary>
    /// <param name="virtualKey">A virtual key code.</param>
    /// <param name="layout">A layout handle, or zero for the calling thread's.</param>
    /// <returns>
    /// A display label. Never empty — falls back to the hex virtual key, which is at
    /// least unambiguous, rather than an empty button the user cannot interpret.
    /// </returns>
    public static unsafe string KeyCapName(int virtualKey, IntPtr layout = default)
    {
        if (virtualKey <= 0) return string.Empty;

        if (FixedNames.TryGetValue(virtualKey, out var fixedName)) return fixedName;

        // F1-F24 read better as themselves than as whatever the layout calls them.
        if (virtualKey is >= 0x70 and <= 0x87) return $"F{virtualKey - 0x6F}";

        // A-Z and 0-9 are identical across the Latin layouts and stable enough
        // elsewhere that asking the layout adds nothing.
        if (virtualKey is >= 'A' and <= 'Z') return ((char)virtualKey).ToString();
        if (virtualKey is >= '0' and <= '9') return ((char)virtualKey).ToString();

        try
        {
            if (layout == IntPtr.Zero) layout = ActiveLayout();

            var scan = Win32Window.MapVirtualKeyEx(
                (uint)virtualKey, Win32Window.MAPVK_VK_TO_VSC_EX, layout);

            if (scan == 0) return $"0x{virtualKey:X2}";

            // MAPVK_VK_TO_VSC_EX returns an 0xE0-prefixed value for extended keys, and
            // GetKeyNameText wants that prefix expressed as bit 24 instead.
            var lParam = (int)((scan & 0xFF) << 16);
            if ((scan & 0xE000) != 0) lParam |= 1 << 24;

            const int capacity = 64;
            var buffer = stackalloc char[capacity];
            var length = Win32Window.GetKeyNameText(lParam, buffer, capacity);

            if (length > 0) return new string(buffer, 0, length);
        }
        catch (Exception)
        {
            // A missing or broken layout must not stop a settings dialog rendering.
        }

        return $"0x{virtualKey:X2}";
    }

    /// <summary>A shortcut written the way the user's own keyboard labels it.</summary>
    public static string DisplayName(ShortcutBinding binding, IntPtr layout = default)
    {
        if (!binding.IsValid) return "None";

        var parts = new List<string>(4);

        // Windows' own order, which is what every other application shows.
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (binding.Modifiers.HasFlag(ShortcutModifiers.Win)) parts.Add("Win");

        parts.Add(KeyCapName(binding.VirtualKey, layout));

        return string.Join(" + ", parts);
    }
}
