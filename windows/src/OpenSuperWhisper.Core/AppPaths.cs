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

    /// <summary>The directory the running executable lives in.</summary>
    public static string InstallDirectory { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Finds a file that ships with the app — a model, a glyph, anything installed
    /// alongside the executable.
    /// </summary>
    /// <param name="fileName">
    /// Name of the shipped copy, looked for next to the executable first.
    /// </param>
    /// <param name="buildTimePath">
    /// Absolute path recorded at build time, used when there is no shipped copy.
    /// </param>
    /// <remarks>
    /// The order is what makes a build both developable and installable from the same
    /// source. In a dev build nothing is copied next to the executable and the
    /// build-time path points into the repository, which is why it has worked so far.
    /// On an installed machine that path names a directory that does not exist — it is
    /// the developer's checkout — so the shipped copy has to win, and has to be looked
    /// for first rather than as a fallback.
    /// </remarks>
    /// <returns>The first path that exists, or <paramref name="buildTimePath"/>.</returns>
    public static string ResolveShippedFile(string fileName, string? buildTimePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var shipped = Path.Combine(InstallDirectory, fileName);
        if (File.Exists(shipped)) return shipped;

        if (!string.IsNullOrWhiteSpace(buildTimePath) && File.Exists(buildTimePath))
        {
            return buildTimePath;
        }

        // Neither exists. Return the shipped location rather than the developer's, so
        // the error names the file the user is actually missing.
        return shipped;
    }
}
