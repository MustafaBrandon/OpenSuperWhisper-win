using Microsoft.Win32;
using OpenSuperWhisper.Core.Diagnostics;

namespace OpenSuperWhisper.Core.Startup;

/// <summary>
/// Starting the app when the user signs in, via <c>HKCU\...\CurrentVersion\Run</c>.
/// </summary>
/// <remarks>
/// <para>
/// Per-user, never machine-wide. <c>HKLM\...\Run</c> would need elevation to set and
/// would start the app for everyone on the machine — wrong for a tool that holds one
/// person's dictation history and microphone choice.
/// </para>
/// <para>
/// The Run key rather than a scheduled task or a startup shortcut: it needs no
/// elevation, no COM, and no file in a folder the user might tidy away, and it is the
/// one mechanism Windows' own Startup Apps settings page can show and disable. A user
/// who turns it off there should not find the app turning it back on, which is why
/// <see cref="IsEnabled"/> reads the registry every time rather than trusting a
/// remembered preference.
/// </para>
/// </remarks>
public sealed class AutostartRegistration
{
    /// <summary>The per-user Run key, relative to HKCU.</summary>
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// The value name, which is what Windows' Startup Apps page shows the user.
    /// </summary>
    public const string ValueName = "OpenSuperWhisper";

    private readonly string _keyPath;
    private readonly string _command;

    /// <param name="executablePath">
    /// The program to start. Defaults to the running executable.
    /// </param>
    /// <param name="keyPath">
    /// Registry key under HKCU. Overridable so tests can exercise the real code against
    /// a scratch key instead of the user's actual startup list.
    /// </param>
    public AutostartRegistration(string? executablePath = null, string? keyPath = null)
    {
        _keyPath = keyPath ?? DefaultKeyPath;

        var path = executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the executable path.");

        // Quoted, always. Program Files has a space in it, and an unquoted path there
        // makes Windows try C:\Program.exe first — the oldest bug in this registry key.
        // The flag keeps a sign-in from putting a window on screen.
        _command = $"\"{path}\" {StartupArgument}";
    }

    /// <summary>
    /// Argument the registered command carries, marking a launch as automatic.
    /// </summary>
    /// <remarks>
    /// Signing in should not open a window, whatever the "start hidden" preference
    /// says: the user did not ask for the app just now, the machine did.
    /// </remarks>
    public const string StartupArgument = "--autostart";

    /// <summary>Whether the app is currently registered to start at sign-in.</summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
                return key?.GetValue(ValueName) is string existing && existing.Length > 0;
            }
            catch (Exception ex)
            {
                Log.Error("could not read the autostart registration", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Whether the registration points somewhere other than this executable.
    /// </summary>
    /// <remarks>
    /// True after the app is moved or reinstalled elsewhere, when the stored command
    /// still names the old location and sign-in silently starts nothing.
    /// </remarks>
    public bool IsStale
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
                return key?.GetValue(ValueName) is string existing && existing != _command;
            }
            catch (Exception ex)
            {
                Log.Error("could not read the autostart registration", ex);
                return false;
            }
        }
    }

    /// <summary>Registers or removes the sign-in entry.</summary>
    /// <returns>False when the registry refused the change.</returns>
    public bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true)
                ?? throw new InvalidOperationException($@"Cannot open HKCU\{_keyPath}.");

            if (enabled)
            {
                key.SetValue(ValueName, _command, RegistryValueKind.String);
                Log.Write($"autostart enabled: {_command}");
            }
            else
            {
                // Deleting a value that is not there is not a failure; it is the state
                // the caller asked for.
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Write("autostart disabled");
            }

            return true;
        }
        catch (Exception ex)
        {
            // Group policy and some managed desktops lock this key. The app still works
            // perfectly well without starting itself, so this is reported, not fatal.
            Log.Error("could not change the autostart registration", ex);
            return false;
        }
    }

    /// <summary>
    /// Re-points an existing registration at the current executable.
    /// </summary>
    /// <remarks>
    /// Called at startup. It deliberately does not create a registration that is not
    /// already there — repairing the user's choice is helpful, inventing one is not.
    /// </remarks>
    public void RepairIfStale()
    {
        if (!IsEnabled || !IsStale) return;

        Log.Write("autostart entry points elsewhere; re-pointing it at this executable");
        Set(true);
    }
}
