using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;

namespace Nagi.Presentation.Services.Implementations;

public class MusicNavigationService(
    ILibraryReader libraryReader,
    INavigationService navigationService,
    ILogger<MusicNavigationService> logger) : IMusicNavigationService
{
    public async Task NavigateToArtistAsync(object? parameter)
    {
        try
        {
            if (await ResolveArtistIdAsync(parameter) is { } artistId)
                navigationService.NavigateTo(new ArtistViewNavigationParameter(artistId));
            else
                logger.LogWarning("Could not resolve artist for navigation from {Parameter}", parameter);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to navigate to the artist of {Parameter}", parameter);
        }
    }

    public async Task NavigateToAlbumAsync(object? parameter)
    {
        try
        {
            if (await ResolveAlbumIdAsync(parameter) is { } albumId)
                navigationService.NavigateTo(new AlbumViewNavigationParameter(albumId));
            else
                logger.LogWarning("Could not resolve album for navigation from {Parameter}", parameter);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to navigate to the album of {Parameter}", parameter);
        }
    }

    private async Task<Guid?> ResolveArtistIdAsync(object? parameter)
    {
        switch (parameter)
        {
            case Artist artist:
                return artist.Id;
            case ArtistViewModelItem artistItem:
                return artistItem.Id;
        }

        var song = parameter switch
        {
            Song value => value,
            ArtistNavigationRequest request => request.Song,
            _ => null
        };
        var artistName = parameter switch
        {
            string value => value,
            ArtistNavigationRequest request => request.ArtistName,
            _ => null
        };

        if (song is not null)
        {
            var artists = song.SongArtists.OrderBy(link => link.Order)
                .Select(link => link.Artist).OfType<Artist>().ToList();
            if (artists.Count == 0)
                artists = (await libraryReader.GetArtistsForSongAsync(song.Id)).ToList();

            // A named request must never fall back to a different primary artist.
            var target = string.IsNullOrEmpty(artistName)
                ? artists.FirstOrDefault()
                : artists.FirstOrDefault(value => value.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase));
            if (target is not null) return target.Id;
        }

        return string.IsNullOrEmpty(artistName) ? null : (await libraryReader.GetArtistByNameAsync(artistName))?.Id;
    }

    private async Task<Guid?> ResolveAlbumIdAsync(object? parameter)
    {
        switch (parameter)
        {
            case Album album:
                return album.Id;
            case AlbumViewModelItem item:
                return item.Id;
            case ArtistAlbumViewModelItem item:
                return item.Id;
            case Song song when (song.Album?.Id ?? song.AlbumId) is { } albumId:
                return albumId;
            case Song song:
                var fullSong = await libraryReader.GetSongByIdAsync(song.Id);
                return fullSong?.Album?.Id ?? fullSong?.AlbumId;
            case Guid requestedId:
                return (await libraryReader.GetAlbumByIdAsync(requestedId))?.Id;
            default:
                return null;
        }
    }
}
