using System.Text;
using Nagi.Core.Models;

namespace Nagi.Core.Helpers;

/// <summary>
///     Provides centralized normalization logic for artist names to ensure consistency across the application.
/// </summary>
public static class ArtistNameHelper
{
    /// <summary>
    ///     Applies Unicode NFC normalization, replaces null characters with a splitting separator,
    ///     and trims whitespace. Returns null for empty names.
    /// </summary>
    public static string? NormalizeStringCore(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;

        var normalized = s.Normalize(NormalizationForm.FormC);

        // Replace null terminators with a visible separator (double backslash) to prevent concatenation
        // and allow for splitting if configured.
        var cleaned = normalized.Replace("\0", "\\\\").Trim();

        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    /// <summary>
    ///     Normalizes an artist name by applying core normalization logic.
    ///     Returns <see cref="Artist.UnknownArtistName"/> if the name is null or whitespace.
    /// </summary>
    public static string Normalize(string? name)
    {
        var normalized = NormalizeStringCore(name);
        return string.IsNullOrEmpty(normalized) ? Artist.UnknownArtistName : normalized;
    }
}
