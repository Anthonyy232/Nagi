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
        if (parameter is Artist artist)
        {
            NavigateToArtist(artist.Id);
            return;
        }
        if (parameter is ArtistViewModelItem artistItem)
        {
            NavigateToArtist(artistItem.Id);
            return;
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
            if (target is not null)
            {
                NavigateToArtist(target.Id);
                return;
            }
        }

        if (!string.IsNullOrEmpty(artistName))
        {
            var target = await libraryReader.GetArtistByNameAsync(artistName);
            if (target is not null)
            {
                NavigateToArtist(target.Id);
                return;
            }
        }

        logger.LogWarning("Could not resolve artist for navigation: {ArtistName}", artistName);
    }

    public async Task NavigateToAlbumAsync(object? parameter)
    {
        Guid? albumId = parameter switch
        {
            Album album => album.Id,
            AlbumViewModelItem item => item.Id,
            ArtistAlbumViewModelItem item => item.Id,
            Song song => song.Album?.Id ?? song.AlbumId,
            _ => null
        };

        if (albumId is null && parameter is Song songWithoutAlbum)
        {
            var fullSong = await libraryReader.GetSongByIdAsync(songWithoutAlbum.Id);
            albumId = fullSong?.Album?.Id ?? fullSong?.AlbumId;
        }
        else if (parameter is Guid requestedId)
            albumId = (await libraryReader.GetAlbumByIdAsync(requestedId))?.Id;

        if (albumId is { } id)
            navigationService.NavigateTo(new AlbumViewNavigationParameter(id));
        else
            logger.LogWarning("Could not resolve album for navigation.");
    }

    private void NavigateToArtist(Guid artistId) =>
        navigationService.NavigateTo(new ArtistViewNavigationParameter(artistId));
}
