using OpenSuperWhisper.Core.Models;
using OpenSuperWhisper.Core.Settings;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Settings defaults and persistence. Defaults are a parity surface with the mac app,
/// so they are asserted rather than assumed.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void Defaults_MatchTheMacApp()
    {
        var s = new AppSettings();

        Assert.Equal("whisper", s.SelectedEngine);
        Assert.Null(s.SelectedWhisperModelPath);
        Assert.Equal("en", s.WhisperLanguage);
        Assert.True(s.SuppressBlankAudio);
        Assert.False(s.ShowTimestamps);
        Assert.Equal(0.0, s.Temperature);
        Assert.Equal(0.6, s.NoSpeechThreshold);
        Assert.Equal(string.Empty, s.InitialPrompt);
        Assert.False(s.UseBeamSearch);
        Assert.Equal(5, s.BeamSize);
        Assert.True(s.UseAsianAutocorrect);
        Assert.True(s.HoldToRecord);
        Assert.False(s.DoublePressToTrigger);
        Assert.True(s.AutoPasteTranscription);
        Assert.False(s.AutoCopyToClipboard);
        Assert.True(s.AddSpaceAfterSentence);
        Assert.False(s.PlaySoundOnRecordStart);
        Assert.False(s.EscCancelWithoutConfirmation);
        Assert.False(s.StartHiddenInTray);
        Assert.False(s.HasCompletedOnboarding);
        Assert.False(s.AutoDeleteRecordingsEnabled);
        Assert.Equal(30, s.AutoDeleteRecordingsAfterDays);
    }

    [Fact]
    public void DefaultTrigger_IsRightControlNotAlt()
    {
        // Deliberate divergence from the mac default of left Command. Right Alt is the
        // closer analogue of Option but is AltGr on many layouts, and the bound key is
        // withheld from other apps.
        var s = new AppSettings();

        Assert.Equal("RightControl", s.ModifierOnlyHotkey);
        Assert.Equal("RightControl", s.LastModifierOnlyHotkey);
        Assert.Equal("None", s.MouseButtonHotkey);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        // A settings dialog edits a copy so Cancel can discard it.
        var original = new AppSettings { WhisperLanguage = "en", BeamSize = 5 };
        var copy = original.Clone();

        copy.WhisperLanguage = "de";
        copy.BeamSize = 8;

        Assert.Equal("en", original.WhisperLanguage);
        Assert.Equal(5, original.BeamSize);
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "osw-settings-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var store = new SettingsStore(SettingsPath);

        Assert.Equal("en", store.Current.WhisperLanguage);
    }

    [Fact]
    public void Update_PersistsAcrossInstances()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(s =>
        {
            s.WhisperLanguage = "de";
            s.BeamSize = 8;
        });

        var reloaded = new SettingsStore(SettingsPath);

        Assert.Equal("de", reloaded.Current.WhisperLanguage);
        Assert.Equal(8, reloaded.Current.BeamSize);
    }

    [Fact]
    public void Update_RaisesChanged()
    {
        var store = new SettingsStore(SettingsPath);
        AppSettings? observed = null;
        store.Changed += (_, s) => observed = s;

        store.Update(s => s.HoldToRecord = false);

        Assert.NotNull(observed);
        Assert.False(observed!.HoldToRecord);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaultsRatherThanThrowing()
    {
        // A damaged settings file must never stop the app starting. Defaults are always
        // a working configuration; half-read settings are not.
        File.WriteAllText(SettingsPath, "{ this is not json");

        var store = new SettingsStore(SettingsPath);

        Assert.Equal("en", store.Current.WhisperLanguage);
    }

    [Fact]
    public void EmptyFile_FallsBackToDefaults()
    {
        File.WriteAllText(SettingsPath, "");

        Assert.Equal("en", new SettingsStore(SettingsPath).Current.WhisperLanguage);
    }

    [Fact]
    public void JsonNull_FallsBackToDefaults()
    {
        // Deserialises successfully to null, which would otherwise surface as a
        // NullReferenceException far from the cause.
        File.WriteAllText(SettingsPath, "null");

        Assert.Equal("en", new SettingsStore(SettingsPath).Current.WhisperLanguage);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFile()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(s => s.BeamSize = 3);

        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }
}

public class ModelCatalogTests
{
    [Fact]
    public void FilenamesDefaultToUrlBasename()
    {
        var model = ModelCatalog.Available.First(m => m.Name == "Turbo V3 large");

        Assert.Equal("ggml-large-v3-turbo.bin", model.ResolvedFilename);
    }

    [Fact]
    public void HebrewModel_OverridesItsFilename()
    {
        // Its URL ends in the generic ggml-model.bin, which would collide with anything
        // else published under that name.
        var model = ModelCatalog.Available.First(m => m.PreferredLanguage == "he");

        Assert.Equal("ggml-ivrit-large-v3-turbo.bin", model.ResolvedFilename);
        Assert.DoesNotContain("ggml-model.bin", model.ResolvedFilename);
    }

    [Fact]
    public void AllFilenamesAreUnique()
    {
        var filenames = ModelCatalog.Available.Select(m => m.ResolvedFilename).ToList();

        Assert.Equal(filenames.Count, filenames.Distinct().Count());
    }

    [Fact]
    public void PreferredLanguageLookup()
    {
        Assert.Equal("he", ModelCatalog.PreferredLanguageFor("ggml-ivrit-large-v3-turbo.bin"));
        Assert.Null(ModelCatalog.PreferredLanguageFor("ggml-large-v3-turbo.bin"));
        Assert.Null(ModelCatalog.PreferredLanguageFor("does-not-exist.bin"));
    }

    [Fact]
    public void GeneralModels_AreAlwaysVisible()
    {
        var model = ModelCatalog.Available.First(m => m.PreferredLanguage is null);

        Assert.True(ModelCatalog.IsVisible(model, isDownloaded: false, "en", "en"));
        Assert.True(ModelCatalog.IsVisible(model, isDownloaded: false, "ja", "fr"));
    }

    [Theory]
    // downloaded, selected, system, expected
    [InlineData(false, "en", "en", false)]  // irrelevant: hidden
    [InlineData(false, "he", "en", true)]   // user selected Hebrew
    [InlineData(false, "en", "he", true)]   // system is Hebrew
    [InlineData(true, "en", "en", true)]    // already downloaded, so keep it listed
    public void LanguageSpecificModel_VisibilityRule(
        bool downloaded, string selected, string system, bool expected)
    {
        var model = ModelCatalog.Available.First(m => m.PreferredLanguage == "he");

        Assert.Equal(expected, ModelCatalog.IsVisible(model, downloaded, selected, system));
    }

    [Fact]
    public void PageUrl_IsDerivedFromDownloadUrl()
    {
        var model = ModelCatalog.Available.First(m => m.Name == "Turbo V3 large");

        Assert.Equal("https://huggingface.co/ggerganov/whisper.cpp", model.PageUrl);
    }

    [Theory]
    [InlineData(574, "574 MB")]
    [InlineData(874, "874 MB")]
    [InlineData(1624, "1.6 GB")]
    public void SizeLabel_IsHumanReadable(int megabytes, string expected)
    {
        var model = new DownloadableModel("x", "https://example.com/x.bin", megabytes, "d");

        Assert.Equal(expected, model.SizeLabel);
    }
}

public class DiskSpaceTests
{
    [Fact]
    public void ThresholdIsTenGigabytes()
    {
        Assert.Equal(10_000_000_000, DiskSpace.RequiredFreeBytes);
    }

    [Theory]
    [InlineData(20_000_000_000, true)]
    [InlineData(10_000_000_000, true)]   // exactly at the threshold is enough
    [InlineData(9_999_999_999, false)]
    [InlineData(0, false)]
    public void HasEnough_ChecksAgainstThreshold(long available, bool expected)
    {
        Assert.Equal(expected, DiskSpace.HasEnough(available));
    }

    [Fact]
    public void Exception_MessageStatesBothNumbers()
    {
        // The user needs to know how much is free and how much is needed, or the error
        // tells them nothing they can act on.
        var ex = new InsufficientDiskSpaceException(1_000_000_000);

        Assert.Equal(1_000_000_000, ex.AvailableBytes);
        Assert.Contains("1 GB free", ex.Message);
        Assert.Contains("10 GB required", ex.Message);
    }

    [Fact]
    public void AvailableBytes_ReportsSomethingForARealPath()
    {
        Assert.True(DiskSpace.AvailableBytes(Path.GetTempPath()) > 0);
    }
}

public class DownloadProgressTests
{
    [Fact]
    public void Fraction_IsNullWhenTotalUnknown()
    {
        // Servers do not always send Content-Length; the UI must show an
        // indeterminate bar rather than a wrong percentage.
        Assert.Null(new DownloadProgress(5000, null).Fraction);
        Assert.Null(new DownloadProgress(5000, 0).Fraction);
    }

    [Fact]
    public void Fraction_ClampsPastCompletion()
    {
        Assert.Equal(1.0, new DownloadProgress(200, 100).Fraction);
    }

    [Theory]
    [InlineData(0, 100, 0.0)]
    [InlineData(50, 100, 0.5)]
    [InlineData(100, 100, 1.0)]
    public void Fraction_IsReceivedOverTotal(long received, long total, double expected)
    {
        Assert.Equal(expected, new DownloadProgress(received, total).Fraction!.Value, 5);
    }
}
