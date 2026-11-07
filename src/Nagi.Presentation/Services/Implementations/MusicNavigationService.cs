using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.Services.Implementations;

public class MusicNavigationService(
    ILibraryReader libraryReader,
    INavigationService navigationService,
    ILogger<MusicNavigationService> logger) : IMusicNavigationService
{
    public void OpenAlbum(Guid albumId) => navigationService.NavigateTo(new AlbumViewNavigationParameter(albumId));

    public async Task OpenAlbumAsync(Song song)
    {
        if (await TryResolveAsync(() => ResolveAlbumIdAsync(song), song, "album") is { } albumId) OpenAlbum(albumId);
    }

    public void OpenArtist(Guid artistId) => navigationService.NavigateTo(new ArtistViewNavigationParameter(artistId));

    public async Task OpenArtistAsync(ArtistNavigationRequest request)
    {
        if (await TryResolveAsync(() => ResolveArtistIdAsync(request), request, "artist") is { } artistId) OpenArtist(artistId);
    }

    public void OpenGenre(Guid genreId, string name) =>
        navigationService.NavigateTo(new GenreViewNavigationParameter { GenreId = genreId, GenreName = name });

    public void OpenFolder(Guid folderId, string name) =>
        navigationService.NavigateTo(new FolderSongViewNavigationParameter { FolderId = folderId, Title = name });

    public void OpenPlaylist(Guid playlistId, string name, string? coverImageUri) =>
        navigationService.NavigateTo(new PlaylistSongViewNavigationParameter
        {
            PlaylistId = playlistId,
            Title = name,
            CoverImageUri = coverImageUri
        });

    public void OpenSmartPlaylist(Guid smartPlaylistId, string name, string? coverImageUri) =>
        navigationService.NavigateTo(new SmartPlaylistSongViewNavigationParameter
        {
            SmartPlaylistId = smartPlaylistId,
            Title = name,
            CoverImageUri = coverImageUri
        });

    // Only the library lookup is caught: a page that fails to build should reach the crash handler like any navigation.
    private async Task<Guid?> TryResolveAsync(Func<Task<Guid?>> resolve, object request, string kind)
    {
        try
        {
            if (await resolve() is { } id) return id;
            logger.LogWarning("Could not resolve the {Kind} to open from {Request}", kind, request);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to look up the {Kind} of {Request}", kind, request);
        }
        return null;
    }

    private async Task<Guid?> ResolveArtistIdAsync(ArtistNavigationRequest request)
    {
        if (request.Song is { } song)
        {
            var artists = song.GetCreditedArtists();
            if (artists.Count == 0)
                artists = (await libraryReader.GetArtistsForSongAsync(song.Id)).ToList();

            // A named request must never fall back to a different primary artist.
            var target = string.IsNullOrEmpty(request.ArtistName)
                ? artists.FirstOrDefault()
                : artists.FirstOrDefault(value => value.Name.Equals(request.ArtistName, StringComparison.OrdinalIgnoreCase));
            if (target is not null) return target.Id;
        }

        return string.IsNullOrEmpty(request.ArtistName)
            ? null
            : (await libraryReader.GetArtistByNameAsync(request.ArtistName))?.Id;
    }

    private async Task<Guid?> ResolveAlbumIdAsync(Song song)
    {
        if ((song.Album?.Id ?? song.AlbumId) is { } albumId) return albumId;
        var fullSong = await libraryReader.GetSongByIdAsync(song.Id);
        return fullSong?.Album?.Id ?? fullSong?.AlbumId;
    }
}
