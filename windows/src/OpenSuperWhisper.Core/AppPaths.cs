namespace OpenSuperWhisper.Core;

/// <summary>
/// Filesystem locations the app uses. Windows equivalents of the mac app's
/// Application Support layout — see docs/windows-port.md §7.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "OpenSuperWhisper";

    /// <summary>%LOCALAPPDATA%\OpenSuperWhisper</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    /// <summary>Whisper model files (.bin).</summary>
    public static string Models => Path.Combine(Root, "models");

    /// <summary>Kept recordings, named by unix timestamp.</summary>
    public static string Recordings => Path.Combine(Root, "recordings");

    /// <summary>settings.json — replaces the mac app's UserDefaults.</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// In-flight captures. Separate from <see cref="Recordings"/> because the queue
    /// distinguishes them: our own temp captures are *moved* into the library on
    /// success, while user-dropped files are *copied* and left in place.
    /// </summary>
    public static string TempRecordings => Path.Combine(
        Path.GetTempPath(), AppFolderName, "recordings");

    /// <summary>Creates every directory above. Safe to call repeatedly.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Recordings);
        Directory.CreateDirectory(TempRecordings);
    }
}
