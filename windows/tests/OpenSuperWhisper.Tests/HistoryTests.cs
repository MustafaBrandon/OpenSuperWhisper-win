using OpenSuperWhisper.Core.History;
using OpenSuperWhisper.Core.Settings;
using Xunit;

namespace OpenSuperWhisper.Tests;

public class RecordingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "osw-history-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingStore _store;

    public RecordingStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new RecordingStore(Path.Combine(_dir, "recordings.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static Recording Make(string text, DateTimeOffset when) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = when,
        FileName = $"{when.ToUnixTimeSeconds()}.wav",
        Transcription = text,
        DurationSeconds = 3.5,
        Status = RecordingStatus.Completed,
        Progress = 1.0,
    };

    [Fact]
    public void EmptyStore_HasNoRecordings()
    {
        Assert.Empty(_store.All());
        Assert.Equal(0, _store.Count());
    }

    [Fact]
    public void Add_ThenRoundTripsEveryField()
    {
        var when = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var recording = Make("hello world", when) with
        {
            SourceFilePath = @"C:\somewhere\input.mp3",
            Status = RecordingStatus.Failed,
            Progress = 0.25,
        };

        _store.Add(recording);
        var loaded = _store.Find(recording.Id);

        Assert.NotNull(loaded);
        Assert.Equal(recording.Id, loaded!.Id);
        Assert.Equal(when.ToUnixTimeSeconds(), loaded.Timestamp.ToUnixTimeSeconds());
        Assert.Equal("hello world", loaded.Transcription);
        Assert.Equal(3.5, loaded.DurationSeconds, 3);
        Assert.Equal(RecordingStatus.Failed, loaded.Status);
        Assert.Equal(0.25, loaded.Progress, 3);
        Assert.Equal(@"C:\somewhere\input.mp3", loaded.SourceFilePath);
    }

    [Fact]
    public void NullSourcePath_StaysNull()
    {
        // Null means "this was a dictation", which drives whether audio is moved or
        // copied. Reading it back as an empty string would lose that distinction.
        var recording = Make("dictated", DateTimeOffset.Now);
        _store.Add(recording);

        Assert.Null(_store.Find(recording.Id)!.SourceFilePath);
    }

    [Fact]
    public void All_ReturnsNewestFirst()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        _store.Add(Make("oldest", now.AddHours(-2)));
        _store.Add(Make("newest", now));
        _store.Add(Make("middle", now.AddHours(-1)));

        var all = _store.All();

        Assert.Equal(["newest", "middle", "oldest"], all.Select(r => r.Transcription));
    }

    [Fact]
    public void All_RespectsLimit()
    {
        var now = DateTimeOffset.Now;
        for (var i = 0; i < 10; i++) _store.Add(Make($"entry {i}", now.AddSeconds(-i)));

        Assert.Equal(3, _store.All(limit: 3).Count);
        Assert.Equal(10, _store.Count());
    }

    [Fact]
    public void Delete_RemovesOne()
    {
        var keep = Make("keep", DateTimeOffset.Now);
        var drop = Make("drop", DateTimeOffset.Now.AddMinutes(-1));
        _store.Add(keep);
        _store.Add(drop);

        Assert.True(_store.Delete(drop.Id));

        Assert.Equal(1, _store.Count());
        Assert.Null(_store.Find(drop.Id));
        Assert.NotNull(_store.Find(keep.Id));
    }

    [Fact]
    public void Delete_UnknownId_ReturnsFalse()
    {
        Assert.False(_store.Delete(Guid.NewGuid()));
    }

    [Fact]
    public void DeleteAll_ClearsEverythingAndReportsCount()
    {
        for (var i = 0; i < 5; i++) _store.Add(Make($"entry {i}", DateTimeOffset.Now.AddSeconds(-i)));

        Assert.Equal(5, _store.DeleteAll());
        Assert.Equal(0, _store.Count());
    }

    [Fact]
    public void DeleteOlderThan_KeepsRecentOnly()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        _store.Add(Make("old", now.AddDays(-40)));
        _store.Add(Make("recent", now.AddDays(-2)));

        var removed = _store.DeleteOlderThan(now.AddDays(-30));

        Assert.Equal(1, removed);
        Assert.Equal(["recent"], _store.All().Select(r => r.Transcription));
    }

    [Fact]
    public void DeleteOlderThan_NothingMatching_IsANoOp()
    {
        var now = DateTimeOffset.Now;
        _store.Add(Make("recent", now));

        Assert.Equal(0, _store.DeleteOlderThan(now.AddDays(-30)));
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void Add_WithSameId_Replaces()
    {
        // Re-transcribing updates a row in place rather than duplicating it.
        var recording = Make("first pass", DateTimeOffset.Now);
        _store.Add(recording);
        _store.Add(recording with { Transcription = "second pass" });

        Assert.Equal(1, _store.Count());
        Assert.Equal("second pass", _store.Find(recording.Id)!.Transcription);
    }

    [Fact]
    public void ReopeningTheDatabase_KeepsData()
    {
        var recording = Make("persisted", DateTimeOffset.Now);
        _store.Add(recording);

        using var reopened = new RecordingStore(Path.Combine(_dir, "recordings.db"));

        Assert.Equal(1, reopened.Count());
        Assert.Equal("persisted", reopened.All()[0].Transcription);
    }
}

public class RetentionPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void CutoffDate_SubtractsDays()
    {
        Assert.Equal(Now.AddDays(-30), RetentionPolicy.CutoffDate(30, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CutoffDate_NonPositiveDays_MeansKeepEverything(int days)
    {
        Assert.Null(RetentionPolicy.CutoffDate(days, Now));
    }

    [Fact]
    public void PathInsideRecordings_IsDeletable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "osw-retention");
        var file = Path.Combine(directory, "123.wav");

        Assert.True(RetentionPolicy.IsDeletablePath(file, directory));
    }

    [Fact]
    public void PathOutsideRecordings_IsNotDeletable()
    {
        // The guard that matters. A stored path could point at a file the user still
        // owns - an imported recording, or a hand-edited value - and automatic deletion
        // must never reach it.
        var directory = Path.Combine(Path.GetTempPath(), "osw-retention");
        var elsewhere = Path.Combine(Path.GetTempPath(), "important", "document.wav");

        Assert.False(RetentionPolicy.IsDeletablePath(elsewhere, directory));
    }

    [Fact]
    public void SiblingDirectoryWithSharedPrefix_IsNotDeletable()
    {
        // "osw-recordings-backup" starts with "osw-recordings", so a naive prefix check
        // would happily delete from it.
        var directory = Path.Combine(Path.GetTempPath(), "osw-recordings");
        var sibling = Path.Combine(Path.GetTempPath(), "osw-recordings-backup", "keep.wav");

        Assert.False(RetentionPolicy.IsDeletablePath(sibling, directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPath_IsNotDeletable(string path)
    {
        Assert.False(RetentionPolicy.IsDeletablePath(path, Path.GetTempPath()));
    }
}

public class HistorySettingTests
{
    [Fact]
    public void HistoryIsOnByDefault()
    {
        // Matches the mac app, which always records. The toggle exists so it can be
        // turned off, not because off is the expected state.
        Assert.True(new AppSettings().SaveTranscriptionHistory);
    }

    [Fact]
    public void HistorySettingPersists()
    {
        var directory = Path.Combine(Path.GetTempPath(), "osw-hist-setting", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");

        try
        {
            new SettingsStore(path).Update(s => s.SaveTranscriptionHistory = false);

            Assert.False(new SettingsStore(path).Current.SaveTranscriptionHistory);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
