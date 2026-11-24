using System.Text;

namespace Nagi.Core.Platform;

public static class TextNormalizer
{
    /// <summary>
    ///     Applies Unicode NFC normalization and trims whitespace. Returns null for blank text.
    /// </summary>
    public static string? Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Normalize(NormalizationForm.FormC).Trim();
}
