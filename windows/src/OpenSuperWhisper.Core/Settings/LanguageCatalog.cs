using System.Globalization;

namespace OpenSuperWhisper.Core.Settings;

/// <summary>
/// Languages the Whisper engine is offered for. Port of the mac app's
/// <c>LanguageUtil</c>.
/// </summary>
/// <remarks>
/// Deliberately a curated subset rather than whisper's full 99. The mac app offers
/// these, and a list of 99 mostly-untested languages is worse for the user than a
/// short list that works — <c>auto</c> still lets whisper detect anything.
/// <para>
/// The Parakeet language lists are absent along with that engine (M8).
/// </para>
/// </remarks>
public static class LanguageCatalog
{
    /// <summary>Whisper's own auto-detect.</summary>
    public const string AutoDetect = "auto";

    public static IReadOnlyList<string> Available { get; } =
    [
        "auto", "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca", "nl", "ar",
        "he", "sv", "it", "id", "hi", "fi", "uk", "ml",
    ];

    private static readonly Dictionary<string, string> Names = new()
    {
        ["auto"] = "Auto-detect",
        ["en"] = "English",
        ["zh"] = "Chinese",
        ["de"] = "German",
        ["es"] = "Spanish",
        ["ru"] = "Russian",
        ["ko"] = "Korean",
        ["fr"] = "French",
        ["ja"] = "Japanese",
        ["pt"] = "Portuguese",
        ["tr"] = "Turkish",
        ["pl"] = "Polish",
        ["ca"] = "Catalan",
        ["nl"] = "Dutch",
        ["ar"] = "Arabic",
        ["he"] = "Hebrew",
        ["sv"] = "Swedish",
        ["it"] = "Italian",
        ["id"] = "Indonesian",
        ["hi"] = "Hindi",
        ["fi"] = "Finnish",
        ["uk"] = "Ukrainian",
        ["ml"] = "Malayalam",
    };

    /// <summary>Display name for a code, falling back to the code itself.</summary>
    public static string DisplayName(string code) =>
        Names.TryGetValue(code, out var name) ? name : code;

    /// <summary>
    /// The user's system language, when the app offers it.
    /// </summary>
    /// <remarks>
    /// Used to decide whether a language-specific model is worth listing — a Hebrew
    /// fine-tune is relevant to someone running Windows in Hebrew even before they
    /// change the transcription language.
    /// </remarks>
    public static string SystemLanguage()
    {
        try
        {
            var twoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return Available.Contains(twoLetter) ? twoLetter : "en";
        }
        catch (Exception)
        {
            return "en";
        }
    }

    /// <summary>Codes paired with display names, auto-detect first and the rest alphabetical.</summary>
    public static IReadOnlyList<(string Code, string Name)> ForDisplay() =>
    [
        (AutoDetect, DisplayName(AutoDetect)),
        .. Available
            .Where(c => c != AutoDetect)
            .Select(c => (Code: c, Name: DisplayName(c)))
            .OrderBy(x => x.Name, StringComparer.CurrentCulture),
    ];
}
