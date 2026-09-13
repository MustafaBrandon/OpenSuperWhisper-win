namespace OpenSuperWhisper.Core.History;

/// <summary>Where a recording is in the pipeline.</summary>
public enum RecordingStatus
{
    /// <summary>Queued, not started.</summary>
    Pending,

    /// <summary>Being decoded to whisper's format.</summary>
    Converting,

    /// <summary>Being transcribed.</summary>
    Transcribing,

    Completed,
    Failed,
}

/// <summary>
/// One dictation or imported file. Port of the mac app's <c>Recording</c>.
/// </summary>
/// <remarks>
/// Field names and the storage shape match the mac schema (plan §7) so fixtures and
/// behaviour can be compared across platforms.
/// </remarks>
public sealed record Recording
{
    public required Guid Id { get; init; }

    /// <summary>When it was captured. Also derives <see cref="FileName"/>.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Audio file name inside the recordings directory.</summary>
    public required string FileName { get; init; }

    /// <summary>The transcript, or a user-facing message when <see cref="Status"/> is Failed.</summary>
    public string Transcription { get; init; } = string.Empty;

    public double DurationSeconds { get; init; }

    public RecordingStatus Status { get; init; } = RecordingStatus.Completed;

    public double Progress { get; init; }

    /// <summary>
    /// Where the audio came from, for queued and imported items.
    /// </summary>
    /// <remarks>
    /// Null once a dictation completes. The distinction drives a real behaviour
    /// difference: our own temp captures are <i>moved</i> into the library, while
    /// user-dropped files are <i>copied</i> so the original stays where they left it.
    /// </remarks>
    public string? SourceFilePath { get; init; }

    /// <summary>Full path to the stored audio.</summary>
    public string AudioPath => System.IO.Path.Combine(AppPaths.Recordings, FileName);

    /// <summary>
    /// The audio this entry can still be played or re-read from, or null if it is gone.
    /// </summary>
    /// <remarks>
    /// Two places to look, in order. A dictation's audio lives in the library under
    /// <see cref="AudioPath"/>. An imported file was never copied there — only its path
    /// was recorded — so it is readable exactly as long as the user leaves it where it
    /// is, which is a promise the app cannot make on their behalf.
    /// </remarks>
    public string? PlayablePath
    {
        get
        {
            if (System.IO.File.Exists(AudioPath)) return AudioPath;
            if (SourceFilePath is { } source && System.IO.File.Exists(source)) return source;

            return null;
        }
    }

    /// <summary>Creates a completed dictation record.</summary>
    public static Recording ForDictation(string transcription, double durationSeconds,
        DateTimeOffset? timestamp = null)
    {
        var when = timestamp ?? DateTimeOffset.Now;

        return new Recording
        {
            Id = Guid.NewGuid(),
            Timestamp = when,
            FileName = $"{when.ToUnixTimeSeconds()}.wav",
            Transcription = transcription,
            DurationSeconds = durationSeconds,
            Status = RecordingStatus.Completed,
            Progress = 1.0,
            SourceFilePath = null,
        };
    }
}

/// <summary>Retention rules for stored recordings.</summary>
public static class RetentionPolicy
{
    /// <summary>Recordings older than this are eligible for deletion.</summary>
    /// <returns>Null when <paramref name="days"/> is not positive, meaning keep everything.</returns>
    public static DateTimeOffset? CutoffDate(int days, DateTimeOffset now) =>
        days > 0 ? now.AddDays(-days) : null;

    /// <summary>
    /// Whether a path may be deleted by retention.
    /// </summary>
    /// <remarks>
    /// Automatic deletion must never reach outside the recordings directory. A stored
    /// path could be anything — an imported file the user still has, or a value edited
    /// by hand — and deleting one would destroy data the app does not own.
    /// </remarks>
    public static bool IsDeletablePath(string path, string? recordingsDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var directory = System.IO.Path.GetFullPath(recordingsDirectory ?? AppPaths.Recordings);
            var full = System.IO.Path.GetFullPath(path);

            if (!directory.EndsWith(System.IO.Path.DirectorySeparatorChar))
            {
                directory += System.IO.Path.DirectorySeparatorChar;
            }

            return full.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // An unparseable path is not one we should be deleting.
            return false;
        }
    }
}
