using OpenSuperWhisper.Core;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Finding the files that ship with the app — the whisper model and the VAD model.
/// </summary>
/// <remarks>
/// This is the difference between a build that runs from the repository and one that
/// runs after being installed. A dev build reads both models straight from the
/// checkout through a path recorded at compile time; on an installed machine that path
/// names somebody else's directory, so the copy beside the executable has to win.
/// </remarks>
public class ShippedFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "osw-shipped-tests", Guid.NewGuid().ToString("N"));

    public ShippedFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Make(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "model");
        return path;
    }

    [Fact]
    public void TheBuildTimePathIsUsedWhenNothingShipped()
    {
        // The development case: nothing is copied next to the executable, and the
        // recorded path points into the repository.
        var checkout = Make("ggml-tiny.en.bin");

        Assert.Equal(checkout, AppPaths.ResolveShippedFile("ggml-tiny.en.bin", checkout));
    }

    [Fact]
    public void AMissingBuildTimePath_ResolvesToTheInstallLocation()
    {
        // The installed case with the file genuinely absent. Naming the install
        // location rather than the developer's checkout is what makes the resulting
        // error message about the user's machine.
        var resolved = AppPaths.ResolveShippedFile(
            "ggml-tiny.en.bin", @"C:\someone-elses-checkout\ggml-tiny.en.bin");

        Assert.Equal(Path.Combine(AppPaths.InstallDirectory, "ggml-tiny.en.bin"), resolved);
    }

    [Fact]
    public void NoBuildTimePathAtAll_IsHandled()
    {
        // Assembly metadata can be absent or empty in a build that did not set it.
        var resolved = AppPaths.ResolveShippedFile("ggml-silero-v5.1.2.bin", null);

        Assert.Equal(
            Path.Combine(AppPaths.InstallDirectory, "ggml-silero-v5.1.2.bin"), resolved);
    }

    [Fact]
    public void TheInstallDirectoryIsWhereTheExecutableIs()
    {
        Assert.True(Directory.Exists(AppPaths.InstallDirectory));
    }

    [Fact]
    public void AnEmptyNameIsRejected()
    {
        Assert.Throws<ArgumentException>(() => AppPaths.ResolveShippedFile(" ", null));
    }
}
