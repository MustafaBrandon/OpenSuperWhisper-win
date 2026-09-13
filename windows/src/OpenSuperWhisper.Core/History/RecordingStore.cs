using Microsoft.Data.Sqlite;
using OpenSuperWhisper.Core.Diagnostics;

namespace OpenSuperWhisper.Core.History;

/// <summary>
/// SQLite-backed recording history. Port of the mac app's GRDB store.
/// </summary>
/// <remarks>
/// The schema matches the mac app's (plan §7). Storage is opt-in per
/// <c>AppSettings.SaveTranscriptionHistory</c>: this class is only asked to store
/// anything when that is on, so disabling it means nothing is written rather than
/// written and hidden.
/// </remarks>
public sealed class RecordingStore : IDisposable
{
    private readonly string _connectionString;
    private readonly Lock _gate = new();
    private bool _disposed;

    public RecordingStore(string? databasePath = null)
    {
        var path = databasePath ?? System.IO.Path.Combine(AppPaths.Root, "recordings.db");

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        DatabasePath = path;
        Initialise();
    }

    public string DatabasePath { get; }

    /// <summary>Raised after the stored set changes, so the history view can refresh.</summary>
    public event EventHandler? Changed;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialise()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();

            command.CommandText = """
                CREATE TABLE IF NOT EXISTS recording (
                    id            TEXT PRIMARY KEY,
                    timestamp     INTEGER NOT NULL,
                    fileName      TEXT    NOT NULL,
                    transcription TEXT    NOT NULL DEFAULT '',
                    duration      REAL    NOT NULL DEFAULT 0,
                    status        TEXT    NOT NULL DEFAULT 'Completed',
                    progress      REAL    NOT NULL DEFAULT 0,
                    sourceFilePath TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_recording_timestamp
                    ON recording (timestamp DESC);
                """;

            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // A history database that cannot be created must not stop the app: dictation
            // and insertion work perfectly well without it.
            Log.Error($"could not initialise the history database at {DatabasePath}", ex);
        }
    }

    public void Add(Recording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);

        Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO recording
                    (id, timestamp, fileName, transcription, duration, status, progress, sourceFilePath)
                VALUES
                    ($id, $timestamp, $fileName, $transcription, $duration, $status, $progress, $sourceFilePath);
                """;

            command.Parameters.AddWithValue("$id", recording.Id.ToString());
            command.Parameters.AddWithValue("$timestamp", recording.Timestamp.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$fileName", recording.FileName);
            command.Parameters.AddWithValue("$transcription", recording.Transcription);
            command.Parameters.AddWithValue("$duration", recording.DurationSeconds);
            command.Parameters.AddWithValue("$status", recording.Status.ToString());
            command.Parameters.AddWithValue("$progress", recording.Progress);
            command.Parameters.AddWithValue("$sourceFilePath",
                (object?)recording.SourceFilePath ?? DBNull.Value);

            command.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Replaces the transcript of an existing recording, leaving its audio untouched.
    /// </summary>
    /// <remarks>
    /// Used when the user re-transcribes an entry with a different model or language.
    /// The row keeps its id and timestamp: this is the same recording heard again, not
    /// a new one, and adding a second row would make history read as if they had
    /// dictated twice.
    /// </remarks>
    /// <returns>False when no such recording exists.</returns>
    public bool UpdateTranscription(Guid id, string transcription)
    {
        ArgumentNullException.ThrowIfNull(transcription);

        var updated = 0;

        Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE recording
                SET transcription = $transcription, status = $status, progress = 1.0
                WHERE id = $id;
                """;

            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$transcription", transcription);
            command.Parameters.AddWithValue("$status", nameof(RecordingStatus.Completed));

            updated = command.ExecuteNonQuery();
        });

        return updated > 0;
    }

    /// <summary>All recordings, newest first.</summary>
    public IReadOnlyList<Recording> All(int limit = 500)
    {
        var results = new List<Recording>();

        Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, timestamp, fileName, transcription, duration, status, progress, sourceFilePath
                FROM recording
                ORDER BY timestamp DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read()) results.Add(Read(reader));
        });

        return results;
    }

    public int Count()
    {
        var count = 0;

        Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM recording;";
            count = Convert.ToInt32(command.ExecuteScalar());
        });

        return count;
    }

    /// <summary>Deletes one recording and its audio file.</summary>
    public bool Delete(Guid id)
    {
        var recording = Find(id);
        if (recording is null) return false;

        Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM recording WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.ExecuteNonQuery();
        });

        DeleteAudio(recording);
        return true;
    }

    /// <summary>Deletes everything, audio included.</summary>
    /// <returns>How many recordings were removed.</returns>
    public int DeleteAll()
    {
        var all = All(int.MaxValue);

        Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM recording;";
            command.ExecuteNonQuery();
        });

        foreach (var recording in all) DeleteAudio(recording);

        return all.Count;
    }

    /// <summary>Deletes recordings older than the retention cutoff.</summary>
    /// <returns>How many were removed.</returns>
    public int DeleteOlderThan(DateTimeOffset cutoff)
    {
        var doomed = new List<Recording>();

        Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, timestamp, fileName, transcription, duration, status, progress, sourceFilePath
                FROM recording
                WHERE timestamp < $cutoff;
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeSeconds());

            using var reader = command.ExecuteReader();
            while (reader.Read()) doomed.Add(Read(reader));
        });

        if (doomed.Count == 0) return 0;

        Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM recording WHERE timestamp < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        });

        foreach (var recording in doomed) DeleteAudio(recording);

        return doomed.Count;
    }

    public Recording? Find(Guid id)
    {
        Recording? found = null;

        Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, timestamp, fileName, transcription, duration, status, progress, sourceFilePath
                FROM recording
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString());

            using var reader = command.ExecuteReader();
            if (reader.Read()) found = Read(reader);
        });

        return found;
    }

    private static Recording Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Timestamp = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
        FileName = reader.GetString(2),
        Transcription = reader.GetString(3),
        DurationSeconds = reader.GetDouble(4),
        Status = Enum.TryParse<RecordingStatus>(reader.GetString(5), out var status)
            ? status
            : RecordingStatus.Completed,
        Progress = reader.GetDouble(6),
        SourceFilePath = reader.IsDBNull(7) ? null : reader.GetString(7),
    };

    /// <summary>Removes a recording's audio, refusing anything outside the library.</summary>
    private static void DeleteAudio(Recording recording)
    {
        try
        {
            var path = recording.AudioPath;

            // The guard matters: a stored path could point at a file the user still
            // owns, and deleting it would destroy data the app never took charge of.
            if (!RetentionPolicy.IsDeletablePath(path)) return;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            Log.Error($"could not delete audio for {recording.Id}", ex);
        }
    }

    private void Execute(Action<SqliteConnection> work)
    {
        if (_disposed) return;

        try
        {
            lock (_gate)
            {
                using var connection = Open();
                work(connection);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // History is a convenience. Losing a write is regrettable; taking the app
            // down over it is not acceptable.
            Log.Error("history database operation failed", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        SqliteConnection.ClearAllPools();
    }
}
