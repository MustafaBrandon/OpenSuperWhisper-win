using OpenSuperWhisper.Core.Diagnostics;
using OpenSuperWhisper.Core.Settings;

namespace OpenSuperWhisper.Core.History;

/// <summary>The outcome of importing one file.</summary>
public readonly record struct ImportResult(string Path, string? Transcript, string? Error)
{
    public bool Succeeded => Error is null && !string.IsNullOrWhiteSpace(Transcript);
}

/// <summary>
/// Transcribes audio files the user hands the app, from a drop or from Explorer.
/// </summary>
/// <remarks>
/// One place for both routes because they are the same operation arriving by two
/// doors, and they can arrive at once — dropping a file while a double-clicked one is
/// still decoding. The queue here is what keeps that from interleaving; one whisper
/// context cannot decode two files at the same time.
/// <para>
/// Imported files are only ever read. Unlike our own captures they are never moved or
/// deleted: they belong to the user and stay where they put them.
/// </para>
/// </remarks>
public sealed class AudioFileImporter
{
    /// <summary>
    /// What Media Foundation will decode. Also the file-association list at M7.
    /// </summary>
    public static readonly string[] AudioExtensions =
        [".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".ogg", ".mp4"];

    // Serialises imports from every source. TranscribeFileAsync waits for the dictation
    // session on its own, but two importers waiting on it would still finish in
    // whichever order the polling happened to break — not the order the user asked for.
    private readonly SemaphoreSlim _queue = new(1, 1);

    private readonly Func<string, Task<string>> _transcribe;
    private readonly RecordingStore? _history;
    private readonly SettingsStore _settings;

    public AudioFileImporter(Func<string, Task<string>> transcribe, RecordingStore? history,
        SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(transcribe);
        ArgumentNullException.ThrowIfNull(settings);

        _transcribe = transcribe;
        _history = history;
        _settings = settings;
    }

    /// <summary>Raised before each file starts, with its name.</summary>
    public event EventHandler<string>? Started;

    /// <summary>Raised after each file finishes, successfully or not.</summary>
    public event EventHandler<ImportResult>? Finished;

    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Keeps the audio files from a list of paths, in the order given.</summary>
    public static List<string> AudioFilesIn(IEnumerable<string> paths) =>
        [.. paths.Where(p => !string.IsNullOrWhiteSpace(p) && IsAudioFile(p) && File.Exists(p))];

    /// <summary>
    /// Transcribes each file in turn, recording the results in history when it is on.
    /// </summary>
    /// <returns>One result per file, in the order they were given.</returns>
    public async Task<IReadOnlyList<ImportResult>> ImportAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var results = new List<ImportResult>();

        foreach (var file in paths)
        {
            await _queue.WaitAsync().ConfigureAwait(true);

            try
            {
                results.Add(await ImportOneAsync(file).ConfigureAwait(true));
            }
            finally
            {
                _queue.Release();
            }
        }

        return results;
    }

    /// <summary>
    /// Transcribes a file without recording anything.
    /// </summary>
    /// <remarks>
    /// For re-running an entry that already exists in history: it needs the same queue,
    /// so it cannot collide with an import, but adding a second row would make one
    /// recording look like two.
    /// </remarks>
    public async Task<string> TranscribeAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await _queue.WaitAsync().ConfigureAwait(true);

        try
        {
            Started?.Invoke(this, Path.GetFileName(path));
            return await _transcribe(path).ConfigureAwait(true);
        }
        finally
        {
            _queue.Release();
        }
    }

    private async Task<ImportResult> ImportOneAsync(string file)
    {
        Started?.Invoke(this, Path.GetFileName(file));

        ImportResult result;

        try
        {
            var text = await _transcribe(file).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(text))
            {
                // Not an error: whisper heard no speech. Kept as a history entry all the
                // same, because an imported file is something the user chose to keep —
                // unlike an empty dictation, which is discarded.
                Log.Write($"imported file produced no speech: {file}");
            }

            Store(file, text);
            result = new ImportResult(file, text, null);
        }
        catch (Exception ex)
        {
            Log.Error($"could not transcribe {file}", ex);
            result = new ImportResult(file, null, ex.Message);
        }

        Finished?.Invoke(this, result);
        return result;
    }

    private void Store(string file, string transcript)
    {
        if (!_settings.Current.SaveTranscriptionHistory) return;
        if (_history is null) return;

        _history.Add(new Recording
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.Now,
            FileName = Path.GetFileName(file),
            Transcription = transcript,
            DurationSeconds = 0,
            Status = RecordingStatus.Completed,
            Progress = 1.0,

            // The path is the whole record of where this audio is: it was never copied
            // into the library, so without it the entry can never be played or redone.
            SourceFilePath = file,
        });
    }
}
