using Nagi.Core.Models;

namespace Nagi.Presentation.Helpers;

/// <summary>
///     The artist names a song or album shows as links, and how those links navigate.
/// </summary>
public static class ArtistLinks
{
    public static List<string> GetNames(Song? song, ICollection<AlbumArtist>? albumArtists, string? artistName)
    {
        var names = (song?.SongArtists.Count > 0
                ? song.GetCreditedArtists().Select(artist => artist.Name)
                : albumArtists?.OrderBy(a => a.Order).Select(a => a.Artist?.Name) ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();

        // A denormalized name is still useful when navigation relationships aren't loaded.
        if (names.Count == 0 && !string.IsNullOrWhiteSpace(artistName)) names.Add(artistName);
        return names;
    }

    /// <summary>
    ///     Whether the links show the song's stored artist name because its artists aren't loaded. Navigation then
    ///     finds the artists from the song rather than by the name.
    /// </summary>
    public static bool ShowsStoredSongName(Song? song, ICollection<AlbumArtist>? albumArtists) =>
        song is not null && !song.SongArtists.Any(link => !string.IsNullOrWhiteSpace(link.Artist?.Name)) &&
        (albumArtists is null || albumArtists.Count == 0);
}
