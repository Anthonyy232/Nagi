using System.Text;

namespace Nagi.Core.Helpers;

public static class TextNormalizer
{
    /// <summary>
    ///     Applies Unicode NFC normalization, replaces null characters with a splitting separator,
    ///     and trims whitespace. Returns null for blank text.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Tags join multiple values with null characters; a visible separator (double backslash) keeps them apart
        // and lets the split characters divide them.
        var cleaned = text.Normalize(NormalizationForm.FormC).Replace("\0", "\\\\").Trim();

        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }
}
