namespace OpenSuperWhisper.Core.Audio;

/// <summary>
/// Removes abandoned temp captures. Port of the mac app's temp cleanup.
/// </summary>
/// <remarks>
/// Recordings that never reached the library — the app was killed mid-transcription,
/// a conversion threw — would otherwise accumulate indefinitely. Runs at startup.
/// </remarks>
public static class TempRecordingSweeper
{
    /// <summary>Temp files older than this are removed.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Deletes stale files from the temp recordings directory.</summary>
    /// <returns>How many files were removed.</returns>
    public static int Sweep() => Sweep(AppPaths.TempRecordings, MaxAge, DateTime.UtcNow);

    /// <summary>Testable core: sweeps <paramref name="directory"/> against a fixed clock.</summary>
    public static int Sweep(string directory, TimeSpan maxAge, DateTime nowUtc)
    {
        // A missing directory is the normal first-run state, not an error.
        if (!Directory.Exists(directory)) return 0;

        var cutoff = nowUtc - maxAge;
        var removed = 0;

        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception)
        {
            return 0;
        }

        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;

                File.Delete(file);
                removed++;
            }
            catch (Exception)
            {
                // Locked by another process, or vanished between listing and delete.
                // Either way the next sweep will get it.
            }
        }

        return removed;
    }
}
