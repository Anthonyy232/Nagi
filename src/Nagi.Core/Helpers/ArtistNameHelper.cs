using System.Text;
using Nagi.Core.Models;

namespace Nagi.Core.Helpers;

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

    public static string Normalize(string? name) => NormalizeStringCore(name) ?? Artist.UnknownArtistName;

    /// <summary>
    ///     Splits each name on any of <paramref name="splitCharacters" />, then normalizes them and drops empty and
    ///     repeated names. Without split characters the names are only normalized.
    /// </summary>
    public static List<string> SplitNames(IEnumerable<string> names, string splitCharacters) =>
        // Split with no separators would split on whitespace.
        names.SelectMany(name => splitCharacters.Length > 0 ? name.Split([.. splitCharacters]) : [name])
            .Select(NormalizeStringCore)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
