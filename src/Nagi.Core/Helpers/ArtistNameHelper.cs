using Nagi.Core.Models;

namespace Nagi.Core.Helpers;

public static class ArtistNameHelper
{
    public static string Normalize(string? name) => TextNormalizer.Normalize(name) ?? Artist.UnknownArtistName;

    /// <summary>
    ///     Splits each name on any of <paramref name="splitCharacters" />, then normalizes them and drops empty and
    ///     repeated names. Without split characters the names are only normalized.
    /// </summary>
    public static List<string> SplitNames(IEnumerable<string> names, string splitCharacters) =>
        // Split with no separators would split on whitespace.
        names.SelectMany(name => splitCharacters.Length > 0 ? name.Split([.. splitCharacters]) : [name])
            .Select(TextNormalizer.Normalize)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
