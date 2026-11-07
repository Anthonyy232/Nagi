using Nagi.Core.Models;
using Nagi.Presentation.Navigation;

namespace Nagi.Presentation.Services.Abstractions;

/// <summary>
///     Opens the page that shows an album, an artist or another collection.
/// </summary>
public interface IMusicNavigationService
{
    void OpenAlbum(Guid albumId);

    /// <summary>Opens the album <paramref name="song" /> is on, looking it up when the song doesn't carry it.</summary>
    Task OpenAlbumAsync(Song song);

    void OpenArtist(Guid artistId);

    /// <summary>
    ///     Opens the artist the request names among the song's credited artists, the song's first credited artist when it
    ///     names none, or the library artist with that name when the song doesn't credit it.
    /// </summary>
    Task OpenArtistAsync(ArtistNavigationRequest request);

    void OpenGenre(Guid genreId, string name);

    void OpenFolder(Guid folderId, string name, bool isServer);

    void OpenPlaylist(Guid playlistId, string name, string? coverImageUri, bool isFromServer);

    void OpenSmartPlaylist(Guid smartPlaylistId, string name, string? coverImageUri);
}
