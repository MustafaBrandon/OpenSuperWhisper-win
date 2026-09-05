using OpenSuperWhisper.Core.Text;
using OpenSuperWhisper.Interop;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Trailing-space post processing. Ported case for case from the mac suite.
/// </summary>
public class TextPostProcessorTests
{
    [Theory]
    [InlineData("Hello world.", "Hello world. ")]
    [InlineData("Hello world?", "Hello world? ")]
    [InlineData("Hello world!", "Hello world! ")]
    [InlineData("Hello world,", "Hello world, ")]
    [InlineData("Hello world:", "Hello world: ")]
    [InlineData("Hello world;", "Hello world; ")]
    [InlineData("Hello world…", "Hello world… ")]
    [InlineData("First. Second.", "First. Second. ")]
    [InlineData(".", ". ")]
    public void TrailingPunctuation_GetsSpace(string input, string expected)
    {
        Assert.Equal(expected, TextPostProcessor.ApplyTrailingSpace(input, enabled: true));
    }

    [Theory]
    [InlineData("Hello world")]
    [InlineData("no trailing punctuation here")]
    [InlineData("123")]
    public void TrailingLetterOrDigit_GetsNoSpace(string input)
    {
        Assert.Equal(input, TextPostProcessor.ApplyTrailingSpace(input, enabled: true));
    }

    [Fact]
    public void Disabled_NeverAddsSpace()
    {
        Assert.Equal("Hello world.", TextPostProcessor.ApplyTrailingSpace("Hello world.", enabled: false));
    }

    [Fact]
    public void EmptyString_IsSafe()
    {
        Assert.Equal("", TextPostProcessor.ApplyTrailingSpace("", enabled: true));
    }
}

/// <summary>
/// Clipboard behaviour, including the restore guard.
/// </summary>
/// <remarks>
/// These touch the real system clipboard, so the fixture snapshots the user's
/// contents up front and puts them back afterwards. Tier 3: needs a desktop session.
/// </remarks>
[Collection("InputHooks")]
public class ClipboardServiceTests : IDisposable
{
    private readonly ClipboardSnapshot? _userClipboard = ClipboardService.Capture();

    public void Dispose()
    {
        // Restore unconditionally: these tests deliberately move the sequence number,
        // so the guard would refuse, and leaving the user's clipboard trashed is not
        // an acceptable side effect of running tests.
        if (_userClipboard is not null)
        {
            ClipboardService.RestoreIfUnchanged(
                _userClipboard, Win32Clipboard.GetClipboardSequenceNumber());
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SetText_ThenGetText_RoundTrips()
    {
        const string value = "osw round trip éü 你好";

        Assert.NotNull(ClipboardService.SetText(value));
        Assert.Equal(value, ClipboardService.GetText());
    }

    [Fact]
    public void SetText_ReturnsSequenceNumberThatMatchesSystem()
    {
        var sequence = ClipboardService.SetText("sequence check");

        Assert.NotNull(sequence);
        Assert.Equal(Win32Clipboard.GetClipboardSequenceNumber(), sequence!.Value);
    }

    [Fact]
    public void RestoreIfUnchanged_UntouchedClipboard_RestoresOriginal()
    {
        ClipboardService.SetText("original contents");
        var snapshot = ClipboardService.Capture();
        Assert.NotNull(snapshot);

        var sequence = ClipboardService.SetText("transcript text");
        Assert.NotNull(sequence);
        Assert.Equal("transcript text", ClipboardService.GetText());

        Assert.True(ClipboardService.RestoreIfUnchanged(snapshot!, sequence!.Value));
        Assert.Equal("original contents", ClipboardService.GetText());
    }

    [Fact]
    public void RestoreIfUnchanged_ClipboardChangedMeanwhile_KeepsNewContents()
    {
        // The case the guard exists for: the user copies something during the 1.5 s
        // restore delay. Restoring anyway would destroy what they just copied.
        ClipboardService.SetText("original contents");
        var snapshot = ClipboardService.Capture();

        var sequence = ClipboardService.SetText("transcript text");
        Assert.NotNull(sequence);

        // Someone else takes the clipboard over.
        ClipboardService.SetText("user copied this");

        Assert.False(ClipboardService.RestoreIfUnchanged(snapshot!, sequence!.Value));
        Assert.Equal("user copied this", ClipboardService.GetText());
    }

    [Fact]
    public void Capture_PreservesUnicodeText()
    {
        const string value = "ünicode — em dash → arrow";
        ClipboardService.SetText(value);

        var snapshot = ClipboardService.Capture();
        Assert.NotNull(snapshot);

        ClipboardService.SetText("something else");
        ClipboardService.RestoreIfUnchanged(snapshot!, Win32Clipboard.GetClipboardSequenceNumber());

        Assert.Equal(value, ClipboardService.GetText());
    }
}

/// <summary>
/// Layout resolution and elevation detection.
/// </summary>
[Collection("InputHooks")]
public class TextInjectorTests
{
    [Fact]
    public void ResolvePasteVirtualKey_ReturnsUsableKey()
    {
        var key = TextInjector.ResolvePasteVirtualKey();

        // On any Latin layout this is 0x56 (QWERTY V). On Dvorak it differs, and on a
        // non-Latin layout VkKeyScanEx fails and the QWERTY fallback applies. Every
        // route must produce a real virtual key rather than 0.
        Assert.NotEqual(0, key);
        Assert.InRange(key, 0x08, 0xFE);
    }

    [Fact]
    public void ResolvePasteVirtualKey_OnLatinLayout_IsQwertyV()
    {
        // The dev machine and CI both run a Latin layout, so this pins the common case.
        // A Dvorak user would legitimately see something else; this is not asserting
        // that every layout maps to 0x56.
        Assert.Equal(0x56, TextInjector.ResolvePasteVirtualKey());
    }

    [Fact]
    public void IsForegroundElevated_FromNormalProcess_DoesNotThrow()
    {
        // The value depends on what happens to be focused during the test run, so the
        // assertion is that the integrity-level walk completes rather than throwing —
        // it dereferences a SID by hand and a mistake there would be an access violation.
        var elevated = TextInjector.IsForegroundElevated();

        Assert.True(elevated || !elevated);
    }

    [Fact]
    public void Insert_EmptyText_DoesNothing()
    {
        Assert.Equal(InsertionResult.NothingToDo,
            TextInjector.Insert("", new InsertionOptions()));
    }

    [Fact]
    public void Insert_BothOptionsOff_DoesNothing()
    {
        // Explicit "insert nowhere" is a supported configuration: the transcript still
        // reaches the history, it just is not inserted or copied.
        var options = new InsertionOptions(Paste: false, CopyToClipboard: false);

        Assert.Equal(InsertionResult.NothingToDo, TextInjector.Insert("some text", options));
    }

    [Fact]
    public void Insert_CopyOnly_PutsTextOnClipboardWithoutPasting()
    {
        var original = ClipboardService.Capture();
        try
        {
            var options = new InsertionOptions(Paste: false, CopyToClipboard: true);

            Assert.Equal(InsertionResult.Success, TextInjector.Insert("copied only.", options));

            // The trailing space rule applies to copy-only as well.
            Assert.Equal("copied only. ", ClipboardService.GetText());
        }
        finally
        {
            if (original is not null)
            {
                ClipboardService.RestoreIfUnchanged(
                    original, Win32Clipboard.GetClipboardSequenceNumber());
            }
        }
    }
}
