using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Input;

/// <summary>
/// A single modifier key usable as a bare trigger.
/// </summary>
/// <remarks>
/// Mirrors the mac app's <c>ModifierKey</c>, mapped to Windows: Command becomes
/// Windows key, Option becomes Alt. <c>Fn</c> is dropped â€” Windows keyboards handle
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

public static class TriggerBindings
{
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
