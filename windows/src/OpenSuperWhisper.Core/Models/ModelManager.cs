using OpenSuperWhisper.Core.Diagnostics;

namespace OpenSuperWhisper.Core.Models;

/// <summary>Raised when free space is too low to attempt a download.</summary>
public sealed class InsufficientDiskSpaceException(long availableBytes)
    : Exception($"Not enough free disk space to download a model. "
        + $"{availableBytes / 1_000_000_000.0:0.#} GB free, "
        + $"{DiskSpace.RequiredFreeBytes / 1_000_000_000.0:0.#} GB required.")
{
    public long AvailableBytes { get; } = availableBytes;
}

/// <summary>Free-space guard for model downloads.</summary>
public static class DiskSpace
{
    /// <summary>
    /// Free space required before a download is allowed.
    /// </summary>
    /// <remarks>
    /// 10 GB, inherited from the mac app, and deliberately far more than the largest
    /// model. Filling the system drive is a much worse outcome than refusing a
    /// download, and a partially written 1.6 GB file on a nearly full disk is the
    /// specific failure this avoids.
    /// </remarks>
    public const long RequiredFreeBytes = 10_000_000_000;

    public static bool HasEnough(long availableBytes) => availableBytes >= RequiredFreeBytes;

    /// <summary>Free bytes on the volume holding the models directory.</summary>
    public static long AvailableBytes(string? path = null)
    {
        try
        {
            var target = path ?? AppPaths.Models;
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(target));
            if (string.IsNullOrEmpty(root)) return long.MaxValue;

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // Unknown free space must not block a download; the write will fail with a
            // real error if the disk is genuinely full.
            return long.MaxValue;
        }
    }

    public static void EnsureEnough(string? path = null)
    {
        var available = AvailableBytes(path);
        if (!HasEnough(available)) throw new InsufficientDiskSpaceException(available);
    }
}

/// <summary>Progress of a model download.</summary>
/// <param name="BytesReceived">Bytes written so far.</param>
/// <param name="TotalBytes">Total, or null when the server does not report a length.</param>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>Completion in 0–1, or null when the total is unknown.</summary>
    public double? Fraction => TotalBytes is > 0
        ? Math.Min(1.0, BytesReceived / (double)TotalBytes.Value)
        : null;
}

/// <summary>
/// Finds, downloads and removes whisper models.
/// Port of the mac app's <c>WhisperModelManager</c>.
/// </summary>
public sealed class ModelManager(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? CreateDefaultClient();

    private static HttpClient CreateDefaultClient() => new()
    {
        // Models are large and connections are sometimes slow. The mac app allows a
        // whole day for the transfer; only the per-response wait is short.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public string ModelsDirectory => AppPaths.Models;

    /// <summary>Model files present on disk, sorted by name.</summary>
    public IReadOnlyList<string> InstalledModelPaths()
    {
        try
        {
            if (!Directory.Exists(ModelsDirectory)) return [];

            return [.. Directory.GetFiles(ModelsDirectory, "*.bin").OrderBy(p => p)];
        }
        catch (Exception ex)
        {
            Log.Error("could not list models", ex);
            return [];
        }
    }

    public bool IsDownloaded(string filename) =>
        File.Exists(System.IO.Path.Combine(ModelsDirectory, filename));

    public string PathFor(string filename) => System.IO.Path.Combine(ModelsDirectory, filename);

    /// <summary>
    /// Copies the bundled model into the models directory when it is missing.
    /// </summary>
    /// <remarks>
    /// Checked on every launch, not just the first. A user who clears the models
    /// directory would otherwise be left with an app that cannot transcribe at all.
    /// </remarks>
    public void EnsureBundledModelPresent(string bundledModelPath)
    {
        try
        {
            var destination = PathFor(ModelCatalog.BundledModelFilename);
            if (File.Exists(destination)) return;
            if (!File.Exists(bundledModelPath)) return;

            Directory.CreateDirectory(ModelsDirectory);
            File.Copy(bundledModelPath, destination);
            Log.Write($"copied bundled model to {destination}");
        }
        catch (Exception ex)
        {
            Log.Error("could not install bundled model", ex);
        }
    }

    /// <summary>
    /// Downloads a model, reporting progress.
    /// </summary>
    /// <remarks>
    /// Streams to a temporary file and moves it into place only on success. A
    /// cancelled or failed download must never leave a truncated <c>.bin</c> that
    /// looks installed — whisper would load it and fail confusingly.
    /// </remarks>
    public async Task DownloadAsync(DownloadableModel model, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var destination = PathFor(model.ResolvedFilename);
        if (File.Exists(destination))
        {
            progress?.Report(new DownloadProgress(1, 1));
            return;
        }

        DiskSpace.EnsureEnough();
        Directory.CreateDirectory(ModelsDirectory);

        var temporary = destination + ".partial";

        try
        {
            using var response = await _http
                .GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // Check the status before writing anything: an error page saved as a .bin
            // is the confusing failure this avoids.
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"The model server returned {(int)response.StatusCode} {response.ReasonPhrase}. "
                    + "Please try again later.");
            }

            var total = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(temporary))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new DownloadProgress(received, total));
                }
            }

            File.Move(temporary, destination, overwrite: true);
            Log.Write($"downloaded {model.Name} to {destination}");
        }
        catch (Exception)
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Deletes a downloaded model.</summary>
    /// <remarks>
    /// Refuses to remove the bundled model: it is the guaranteed fallback, and deleting
    /// it could leave the app with no usable model at all.
    /// </remarks>
    public bool Delete(string filename)
    {
        if (filename == ModelCatalog.BundledModelFilename) return false;

        try
        {
            var path = PathFor(filename);
            if (!File.Exists(path)) return false;

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"could not delete model {filename}", ex);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover .partial is harmless; it is never mistaken for a model.
        }
    }
}
