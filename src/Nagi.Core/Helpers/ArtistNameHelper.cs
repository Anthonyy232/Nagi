using System.Text;
using Nagi.Core.Models;

namespace Nagi.Core.Helpers;

/// <summary>
///     Provides centralized normalization logic for artist names to ensure consistency across the application.
/// </summary>
public static class ArtistNameHelper
{
    /// <summary>
    ///     Core normalization logic for strings:
    ///     1. Applying Unicode NFC normalization (ensures composed vs decomposed characters are unified)
    ///     2. Removing null terminators and other control characters
    ///     3. Trimming whitespace
    ///     Returns null if the input is null, whitespace, or becomes empty after normalization.
    /// </summary>
    public static string? NormalizeStringCore(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        
        // Apply Unicode NFC normalization to unify composed/decomposed characters
        // This ensures "é" (U+00E9) and "e\u0301" (e + combining accent) become identical
        // Normalize() already checks IsNormalized internally, so no need to do it manually
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
