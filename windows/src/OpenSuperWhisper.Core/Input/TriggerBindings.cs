using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Input;

/// <summary>
/// A single modifier key usable as a bare trigger.
/// </summary>
/// <remarks>
/// Mirrors the mac app's <c>ModifierKey</c>, mapped to Windows: Command becomes
/// Windows key, Option becomes Alt. <c>Fn</c> is dropped — Windows keyboards handle
/// it in firmware and it never reaches a hook.
/// </remarks>
public enum ModifierKey
{
    None,
    LeftWin,
    RightWin,
    LeftAlt,
    RightAlt,
    LeftShift,
    RightShift,
    LeftControl,
    RightControl,
}

public enum MouseButton
{
    None,
    Middle,
    Button4,
    Button5,
}

/// <summary>
/// The modifiers held alongside a shortcut's main key.
/// </summary>
/// <remarks>
/// Side-agnostic, unlike <see cref="ModifierKey"/>: a user pressing Ctrl+Space does not
/// care which Ctrl, and demanding a specific one would make the shortcut fail for
/// left-handed and right-handed users in turn.
/// </remarks>
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A key combination trigger, e.g. Alt+`. The third trigger mode, after a bare
/// modifier and a mouse button.
/// </summary>
/// <remarks>
/// <para>
/// At least one modifier is required. The bound key is withheld from other
/// applications while it is bound, so a bare letter or digit would take that character
/// away from the user's keyboard entirely — a trap the settings UI should not let
/// anyone walk into.
/// </para>
/// <para>
/// Persisted with the virtual key in hex — <c>Alt+0xC0</c> — rather than a key name.
/// Key names are layout-dependent: the key that says <c>`</c> on a US layout is
/// <c>^</c> on a German one, so storing the name would silently rebind the shortcut
/// when the user switched layouts. The virtual key is stable; only the label changes,
/// and the label is resolved for display at the time it is shown.
/// </para>
/// </remarks>
public readonly record struct ShortcutBinding(int VirtualKey, ShortcutModifiers Modifiers)
{
    /// <summary>Nothing bound.</summary>
    public static ShortcutBinding None => new(0, ShortcutModifiers.None);

    /// <summary>
    /// The default suggestion, Alt+` — the direct translation of the mac app's
    /// Option+`, and free in most Windows applications.
    /// </summary>
    public static ShortcutBinding Suggested => new(VkOem3, ShortcutModifiers.Alt);

    /// <summary>VK_OEM_3: the backtick key on a US layout.</summary>
    public const int VkOem3 = 0xC0;

    public bool IsValid => VirtualKey != 0 && Modifiers != ShortcutModifiers.None;

    /// <summary>Round-trippable form for the settings file.</summary>
    public override string ToString()
    {
        if (!IsValid) return string.Empty;

        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Win)) parts.Add("Win");

        parts.Add($"0x{VirtualKey:X2}");

        return string.Join("+", parts);
    }

    /// <summary>
    /// Parses the stored form. Anything unrecognised yields <see cref="None"/>.
    /// </summary>
    /// <remarks>
    /// Tolerant by design — this reads a hand-editable file, and the cost of a bad
    /// value is falling back to the default trigger rather than starting with no
    /// trigger at all.
    /// </remarks>
    public static ShortcutBinding Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;

        var modifiers = ShortcutModifiers.None;
        var virtualKey = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ShortcutModifiers.Control; continue;
                case "alt": modifiers |= ShortcutModifiers.Alt; continue;
                case "shift": modifiers |= ShortcutModifiers.Shift; continue;
                case "win" or "windows": modifiers |= ShortcutModifiers.Win; continue;
            }

            var isHex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

            var parsed = isHex
                ? int.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var hex) ? hex : 0
                : int.TryParse(raw, out var dec) ? dec : 0;

            // Virtual keys are a single byte. A larger number is a corrupt value, not a
            // key, and binding it would produce a shortcut nothing can press.
            if (parsed is > 0 and <= 0xFF) virtualKey = parsed;
        }

        var binding = new ShortcutBinding(virtualKey, modifiers);
        return binding.IsValid ? binding : None;
    }
}

/// <summary>
/// The one trigger a settings file resolves to, with the others discarded.
/// </summary>
/// <remarks>
/// Only <see cref="Mode"/> decides which of the remaining fields means anything.
/// </remarks>
public readonly record struct ResolvedTrigger(
    TriggerMode Mode,
    ModifierKey Key,
    MouseButton Button,
    ShortcutBinding Shortcut);

public static class TriggerBindings
{
    /// <summary>
    /// Resolves the settings to exactly one live trigger.
    /// </summary>
    /// <remarks>
    /// Precedence is inherited from the mac app: a mouse button beats a bare modifier
    /// key, which beats a shortcut. The settings dialog clears the others when one is
    /// chosen, so in practice only one is ever set — but a hand-edited file can set
    /// all three, and silently picking a different one than the dialog shows would be
    /// its own bug.
    /// <para>
    /// The fallback matters as much as the order. A file with every trigger cleared
    /// would otherwise leave the app unable to start a recording, with nothing on
    /// screen to explain why, so it lands on the default instead of on nothing.
    /// </para>
    /// </remarks>
    public static ResolvedTrigger Resolve(Settings.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (Enum.TryParse<MouseButton>(settings.MouseButtonHotkey, out var button)
            && button != MouseButton.None)
        {
            return new ResolvedTrigger(TriggerMode.MouseButton, ModifierKey.None, button,
                ShortcutBinding.None);
        }

        if (Enum.TryParse<ModifierKey>(settings.ModifierOnlyHotkey, out var key)
            && key != ModifierKey.None)
        {
            return new ResolvedTrigger(TriggerMode.ModifierKey, key, MouseButton.None,
                ShortcutBinding.None);
        }

        var shortcut = ShortcutBinding.Parse(settings.ShortcutHotkey);
        if (shortcut.IsValid)
        {
            return new ResolvedTrigger(TriggerMode.Shortcut, ModifierKey.None, MouseButton.None,
                shortcut);
        }

        return new ResolvedTrigger(TriggerMode.ModifierKey, ModifierKey.RightControl,
            MouseButton.None, ShortcutBinding.None);
    }

    public static int ToVirtualKey(this ModifierKey key) => key switch
    {
        ModifierKey.LeftWin => Win32Input.VK_LWIN,
        ModifierKey.RightWin => Win32Input.VK_RWIN,
        ModifierKey.LeftAlt => Win32Input.VK_LMENU,
        ModifierKey.RightAlt => Win32Input.VK_RMENU,
        ModifierKey.LeftShift => Win32Input.VK_LSHIFT,
        ModifierKey.RightShift => Win32Input.VK_RSHIFT,
        ModifierKey.LeftControl => Win32Input.VK_LCONTROL,
        ModifierKey.RightControl => Win32Input.VK_RCONTROL,
        _ => 0,
    };

    public static string DisplayName(this ModifierKey key) => key switch
    {
        ModifierKey.None => "None",
        ModifierKey.LeftWin => "Left Win",
        ModifierKey.RightWin => "Right Win",
        ModifierKey.LeftAlt => "Left Alt",
        ModifierKey.RightAlt => "Right Alt",
        ModifierKey.LeftShift => "Left Shift",
        ModifierKey.RightShift => "Right Shift",
        ModifierKey.LeftControl => "Left Ctrl",
        ModifierKey.RightControl => "Right Ctrl",
        _ => key.ToString(),
    };

    public static string DisplayName(this MouseButton button) => button switch
    {
        MouseButton.None => "None",
        MouseButton.Middle => "Button 3 (Middle)",
        MouseButton.Button4 => "Button 4 (Back)",
        MouseButton.Button5 => "Button 5 (Forward)",
        _ => button.ToString(),
    };

    /// <summary>Mouse buttons a user can actually bind.</summary>
    /// <remarks>
    /// Left and right are absent on purpose: binding either would make the mouse
    /// unusable for everything else.
    /// </remarks>
    public static IReadOnlyList<MouseButton> SelectableButtons { get; } =
        [MouseButton.Middle, MouseButton.Button4, MouseButton.Button5];

    public static IReadOnlyList<ModifierKey> SelectableModifiers { get; } =
    [
        ModifierKey.LeftWin, ModifierKey.RightWin,
        ModifierKey.LeftAlt, ModifierKey.RightAlt,
        ModifierKey.LeftShift, ModifierKey.RightShift,
        ModifierKey.LeftControl, ModifierKey.RightControl,
    ];
}
