namespace OpenSuperWhisper.Core.Text;

/// <summary>
/// Final touches applied to a transcript before it is inserted.
/// </summary>
public static class TextPostProcessor
{
    /// <summary>
    /// Appends a space when the text ends in punctuation, so consecutive dictations
    /// run together as prose rather than colliding.
    /// </summary>
    /// <remarks>
    /// Named for sentences, but the mac implementation tests for <i>any</i> trailing
    /// punctuation, not just sentence terminators — a trailing comma or colon gets a
    /// space too. That is the behaviour users have, so it is the behaviour kept here.
    /// </remarks>
    public static string ApplyTrailingSpace(string text, bool enabled)
    {
        if (!enabled || string.IsNullOrEmpty(text)) return text;

        return char.IsPunctuation(text[^1]) ? text + " " : text;
    }
}
