using OpenSuperWhisper.Core.History;
using OpenSuperWhisper.Core.Settings;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Files the user hands the app, from a drop or from Explorer.
/// </summary>
/// <remarks>
/// Both routes share one queue because they can arrive at once — dropping a file while
/// a double-clicked one is still decoding — and one whisper context cannot serve two.
/// </remarks>
public class AudioFileImporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "osw-import-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingStore _store;
    private readonly SettingsStore _settings;

    public AudioFileImporterTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new RecordingStore(Path.Combine(_dir, "recordings.db"));
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string MakeFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46]);
        return path;
    }

    private AudioFileImporter Importer(Func<string, Task<string>> transcribe) =>
        new(transcribe, _store, _settings);

    [Fact]
    public void OnlyAudioFilesThatExistAreAccepted()
    {
        // A drop can carry anything: a folder, a screenshot, a shortcut. Filtering here
        // is what makes the drop target refuse rather than fail later.
        var audio = MakeFile("clip.mp3");
        var document = MakeFile("notes.txt");
        var missing = Path.Combine(_dir, "gone.wav");

        var accepted = AudioFileImporter.AudioFilesIn([audio, document, missing]);

        Assert.Equal([audio], accepted);
    }

    [Fact]
    public async Task FilesAreTranscribedInTheOrderGiven()
    {
        // The order the user dropped them in is the order they expect the results.
        var files = new[] { MakeFile("a.wav"), MakeFile("b.wav"), MakeFile("c.wav") };
        var seen = new List<string>();

        var importer = Importer(path =>
        {
            seen.Add(Path.GetFileName(path));
            return Task.FromResult($"text for {Path.GetFileName(path)}");
        });

        await importer.ImportAsync(files);

        Assert.Equal(["a.wav", "b.wav", "c.wav"], seen);
    }

    [Fact]
    public async Task ImportedFilesAreRecordedWithTheirSourcePath()
    {
        // The path is the whole record of where the audio is — it was never copied into
        // the library, so without it the entry can never be played or re-transcribed.
        var file = MakeFile("interview.m4a");

        await Importer(_ => Task.FromResult("the transcript")).ImportAsync([file]);

        var stored = Assert.Single(_store.All());
        Assert.Equal("the transcript", stored.Transcription);
        Assert.Equal(file, stored.SourceFilePath);
        Assert.Equal("interview.m4a", stored.FileName);
    }

    [Fact]
    public async Task AnImportedFileWithNoSpeechIsStillKept()
    {
        // The opposite of the dictation rule, and deliberately so: an empty dictation is
        // an accident, while an imported file is something the user chose. Discarding it
        // would look like the import silently failed.
        var file = MakeFile("silence.wav");

        var results = await Importer(_ => Task.FromResult(string.Empty)).ImportAsync([file]);

        Assert.Single(_store.All());
        Assert.False(results[0].Succeeded);
        Assert.Null(results[0].Error);
    }

    [Fact]
    public async Task AFailureIsReportedAndDoesNotStopTheRest()
    {
        // One unreadable file in a drop of five must not cost the other four.
        var bad = MakeFile("broken.wav");
        var good = MakeFile("fine.wav");

        var importer = Importer(path => Path.GetFileName(path) == "broken.wav"
            ? throw new InvalidDataException("not decodable")
            : Task.FromResult("recovered"));

        var results = await importer.ImportAsync([bad, good]);

        Assert.Equal("not decodable", results[0].Error);
        Assert.True(results[1].Succeeded);
        Assert.Single(_store.All());
    }

    [Fact]
    public async Task WithHistoryOff_NothingIsWritten()
    {
        // "Off" means not recorded, not recorded-and-hidden — the same promise the
        // dictation path makes.
        _settings.Update(s => s.SaveTranscriptionHistory = false);

        await Importer(_ => Task.FromResult("not to be kept")).ImportAsync([MakeFile("private.wav")]);

        Assert.Empty(_store.All());
    }

    [Fact]
    public async Task TranscribeAsync_DoesNotRecordAnything()
    {
        // Used by re-transcribe, which updates the existing row. A second row would make
        // one recording look like two.
        var text = await Importer(_ => Task.FromResult("again")).TranscribeAsync(MakeFile("old.wav"));

        Assert.Equal("again", text);
        Assert.Empty(_store.All());
    }

    [Fact]
    public async Task ImportsDoNotOverlap()
    {
        // The queue is the point: whisper decoding two files at once corrupts both.
        var running = 0;
        var overlapped = false;

        var importer = Importer(async _ =>
        {
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return "done";
        });

        var files = new[] { MakeFile("1.wav"), MakeFile("2.wav") };

        await Task.WhenAll(
            importer.ImportAsync([files[0]]),
            importer.ImportAsync([files[1]]));

        Assert.False(overlapped, "two imports decoded at the same time");
    }
}
