using Nagi.Core.Helpers;

namespace Nagi.Core.Library;

public static class ArtistNameHelper
{
    public static string Normalize(string? name) => TextNormalizer.Normalize(name) ?? Artist.UnknownArtistName;

    /// <summary>
    ///     The album artists a song's tags give its album: their own, or else the song's artists, or else
    ///     <see cref="Artist.UnknownArtistName" />.
    /// </summary>
    /// <param name="trackArtistNames">The song's artists, already normalized.</param>
    public static List<string> NormalizeAlbumArtists(IEnumerable<string> albumArtistNames,
        IReadOnlyList<string> trackArtistNames)
    {
        var names = albumArtistNames.Select(Normalize).ToList();
        if (names.Count > 0) return names;
        return trackArtistNames.Count > 0 ? [.. trackArtistNames] : [Artist.UnknownArtistName];
    }

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
