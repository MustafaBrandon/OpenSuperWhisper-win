namespace OpenSuperWhisper.Core.Models;

/// <summary>A whisper model available for download.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Url">Direct download URL.</param>
/// <param name="SizeMegabytes">Approximate download size, for the UI and the space check.</param>
/// <param name="Description">One-line summary of the trade it makes.</param>
/// <param name="Filename">On-disk name. Defaults to the URL's last segment.</param>
/// <param name="PreferredLanguage">
/// Set for language-specific fine-tunes. Selecting the model forces this language,
/// and it also gates whether the model is listed at all.
/// </param>
public sealed record DownloadableModel(
    string Name,
    string Url,
    int SizeMegabytes,
    string Description,
    string? Filename = null,
    string? PreferredLanguage = null)
{
    /// <summary>
    /// On-disk filename.
    /// </summary>
    /// <remarks>
    /// Defaults to the URL basename, but the Hebrew fine-tune must override it: its URL
    /// ends in the generic <c>ggml-model.bin</c>, which would collide with any other
    /// model published under the same name.
    /// </remarks>
    public string ResolvedFilename => Filename ?? DeriveFilename(Url);

    private static string DeriveFilename(string url)
    {
        var withoutQuery = url.Split('?')[0];
        var lastSlash = withoutQuery.LastIndexOf('/');
        return lastSlash >= 0 ? withoutQuery[(lastSlash + 1)..] : withoutQuery;
    }

    /// <summary>Human-readable size, e.g. "1.6 GB".</summary>
    public string SizeLabel => SizeMegabytes >= 1000
        ? $"{SizeMegabytes / 1000.0:0.#} GB"
        : $"{SizeMegabytes} MB";

    /// <summary>
    /// The model's Hugging Face page, derived by trimming the download path.
    /// </summary>
    public string? PageUrl
    {
        get
        {
            var marker = Url.IndexOf("/resolve/", StringComparison.Ordinal);
            return marker < 0 ? null : Url[..marker];
        }
    }
}

/// <summary>The models the app offers. Mirrors the mac app's catalog.</summary>
public static class ModelCatalog
{
    /// <summary>Bundled with the installer; always present, never downloaded.</summary>
    public const string BundledModelFilename = "ggml-tiny.en.bin";

    public static IReadOnlyList<DownloadableModel> Available { get; } =
    [
        new(
            Name: "Turbo V3 large",
            Url: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin?download=true",
            SizeMegabytes: 1624,
            Description: "High accuracy, best quality"),

        new(
            Name: "Turbo V3 medium",
            Url: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q8_0.bin?download=true",
            SizeMegabytes: 874,
            Description: "Balanced speed and accuracy"),

        new(
            Name: "Turbo V3 small",
            Url: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin?download=true",
            SizeMegabytes: 574,
            Description: "Fastest processing"),

        new(
            Name: "Turbo V3 Hebrew",
            Url: "https://huggingface.co/ivrit-ai/whisper-large-v3-turbo-ggml/resolve/main/ggml-model.bin?download=true",
            SizeMegabytes: 1624,
            Description: "Hebrew fine-tune of Turbo V3 by ivrit.ai. Sets the language to Hebrew.",
            Filename: "ggml-ivrit-large-v3-turbo.bin",
            PreferredLanguage: "he"),
    ];

    /// <summary>The language a model forces, if any.</summary>
    /// <remarks>
    /// These fine-tunes require the language to be set explicitly; leaving it on
    /// auto-detect produces markedly worse output.
    /// </remarks>
    public static string? PreferredLanguageFor(string filename) =>
        Available.FirstOrDefault(m => m.ResolvedFilename == filename)?.PreferredLanguage;

    /// <summary>
    /// Whether a model should be listed.
    /// </summary>
    /// <remarks>
    /// Language-specific models stay hidden unless they are relevant: already
    /// downloaded, or matching the selected or system language. Otherwise a Hebrew
    /// fine-tune sits in every English user's list forever, which is noise.
    /// </remarks>
    public static bool IsVisible(DownloadableModel model, bool isDownloaded,
        string selectedLanguage, string systemLanguage)
    {
        if (model.PreferredLanguage is not { } language) return true;
        if (isDownloaded) return true;

        return selectedLanguage == language || systemLanguage == language;
    }
}
