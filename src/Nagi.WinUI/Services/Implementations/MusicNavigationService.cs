using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Provides specialized navigation logic for music entities, handling database lookups
///     and fallback logic to ensure robust navigation.
/// </summary>
public class MusicNavigationService : IMusicNavigationService
{
    private readonly ILibraryReader _libraryReader;
    private readonly INavigationService _navigationService;
    private readonly ILogger<MusicNavigationService> _logger;

    public MusicNavigationService(
        ILibraryReader libraryReader,
        INavigationService navigationService,
        ILogger<MusicNavigationService> logger)
    {
        _libraryReader = libraryReader;
        _navigationService = navigationService;
        _logger = logger;
    }

    public async Task NavigateToArtistAsync(object? parameter)
    {
        _logger.LogDebug("NavigateToArtistAsync invoked with parameter type: {ParamType}", 
            parameter?.GetType().Name ?? "null");

        Song? targetSong = null;
        string? targetArtistName = null;

        if (parameter is Song song)
        {
            targetSong = song;
        }
        else if (parameter is ArtistNavigationRequest request)
        {
            targetSong = request.Song;
            targetArtistName = request.ArtistName;
            _logger.LogDebug("Request parsed: ArtistName='{ArtistName}', SongId={SongId}", 
                targetArtistName, targetSong?.Id);
        }
        else if (parameter is string artistName)
        {
            targetArtistName = artistName;
        }

        else if (parameter is Artist artist)
        {
            Navigate(artist);
            return;
        }
        else if (parameter is ArtistViewModelItem artistVm)
        {
            _logger.LogDebug("Navigating via ArtistViewModelItem: {ArtistName}", artistVm.Name);
            var navParam = new ArtistViewNavigationParameter
            {
                ArtistId = artistVm.Id,
                ArtistName = artistVm.Name
            };
            _navigationService.Navigate(typeof(ArtistViewPage), navParam);
            return;
        }

        // If we have a song, prioritize it to get the artist
        if (targetSong != null)
        {
            _logger.LogDebug("Attempting to resolve artist via song: {SongId}", targetSong.Id);
            
            // Ensure we have artist details. In list views, SongArtists is excluded for performance.
            if (targetSong.SongArtists == null || !targetSong.SongArtists.Any())
            {
                _logger.LogDebug("Fetching full song details for navigation to artist (SongId: {SongId})", targetSong.Id);
                var fullSong = await _libraryReader.GetSongByIdAsync(targetSong.Id).ConfigureAwait(true);
                if (fullSong != null) targetSong = fullSong;
            }

            Artist? targetArtist = null;

            if (!string.IsNullOrEmpty(targetArtistName))
            {
                // Find the specific artist by name
                targetArtist = targetSong.SongArtists?
                    .FirstOrDefault(sa => sa.Artist?.Name?.Equals(targetArtistName, StringComparison.OrdinalIgnoreCase) == true)?
                    .Artist;
                
                if (targetArtist == null) 
                    _logger.LogDebug("Artist '{ArtistName}' not found in SongArtists collection for this song.", targetArtistName);
                else
                    _logger.LogDebug("Found artist '{ArtistName}' in SongArtists collection.", targetArtistName);
            }

            // Fallback to primary artist ONLY if no name was specified. 
            // If a name WAS specified but not found in this song's metadata, 
            // we should proceed to a global name lookup instead of jumping to the song's primary artist.
            if (targetArtist == null && string.IsNullOrEmpty(targetArtistName))
            {
                targetArtist = targetSong.SongArtists?.OrderBy(sa => sa.Order).FirstOrDefault()?.Artist;
                _logger.LogDebug("Falling back to primary artist: {ArtistName}", targetArtist?.Name);
            }

            if (targetArtist != null)
            {
                Navigate(targetArtist);
                return;
            }
        }
        
        // Final fallback: Try looking up by name directly in the database
        if (!string.IsNullOrEmpty(targetArtistName))
        {
             _logger.LogDebug("Attempting to resolve artist by global name lookup: '{ArtistName}'", targetArtistName);
             var artist = await _libraryReader.GetArtistByNameAsync(targetArtistName);
             if (artist != null)
             {
                 _logger.LogDebug("Artist found by name. Navigating.");
                 Navigate(artist);
                 return;
             }
             
             _logger.LogWarning("Artist '{ArtistName}' not found in database via name lookup.", targetArtistName);
        }

        _logger.LogWarning("Could not navigate to artist: No valid context or artist not found (Name: '{ArtistName}')", 
            targetArtistName ?? "null");
    }

    public async Task NavigateToAlbumAsync(object? parameter)
    {
        _logger.LogDebug("NavigateToAlbumAsync invoked with parameter type: {ParamType}", 
            parameter?.GetType().Name ?? "null");

        if (parameter is Album album)
        {
            Navigate(album);
            return;
        }

        if (parameter is AlbumViewModelItem albumVm)
        {
            _logger.LogDebug("Navigating via AlbumViewModelItem: {AlbumTitle}", albumVm.Title);
            var navParam = new AlbumViewNavigationParameter
            {
                AlbumId = albumVm.Id,
                AlbumTitle = albumVm.Title,
                ArtistName = albumVm.ArtistName
            };
            _navigationService.Navigate(typeof(AlbumViewPage), navParam);
            return;
        }

        if (parameter is ArtistAlbumViewModelItem artistAlbumVm)
        {
            _logger.LogDebug("Navigating via ArtistAlbumViewModelItem: {AlbumTitle}", artistAlbumVm.Name);
            var navParam = new AlbumViewNavigationParameter
            {
                AlbumId = artistAlbumVm.Id,
                AlbumTitle = artistAlbumVm.Name,
                // ArtistAlbumViewModelItem doesn't have ArtistName, but we can resolve it or the target page will load it.
                // We'll leave it empty and let the target page load the full metadata.
                ArtistName = string.Empty 
            };
            _navigationService.Navigate(typeof(AlbumViewPage), navParam);
            return;
        }

        if (parameter is Song song)
        {
            if (song.Album != null)
            {
                Navigate(song.Album);
                return;
            }

            _logger.LogDebug("Song has no album object; fetching full song details (SongId: {SongId})", song.Id);
            var fullSong = await _libraryReader.GetSongByIdAsync(song.Id).ConfigureAwait(true);
            if (fullSong?.Album != null)
            {
                Navigate(fullSong.Album);
                return;
            }
        }

        if (parameter is Guid albumId)
        {
            _logger.LogDebug("Resolving album by ID: {AlbumId}", albumId);
            var resolvedAlbum = await _libraryReader.GetAlbumByIdAsync(albumId).ConfigureAwait(true);
            if (resolvedAlbum != null)
            {
                Navigate(resolvedAlbum);
                return;
            }
        }

        _logger.LogWarning("Could not navigate to album: No valid context or album not found.");
    }

    private void Navigate(Artist artist)
    {
        _logger.LogDebug("Navigating to artist '{ArtistName}' ({ArtistId})", artist.Name, artist.Id);
        var navParam = new ArtistViewNavigationParameter
        {
            ArtistId = artist.Id,
            ArtistName = artist.Name
        };
        _navigationService.Navigate(typeof(ArtistViewPage), navParam);
    }

    private void Navigate(Album album)
    {
        _logger.LogDebug("Navigating to album '{AlbumTitle}' ({AlbumId})", album.Title, album.Id);
        var navParam = new AlbumViewNavigationParameter
        {
            AlbumId = album.Id,
            AlbumTitle = album.Title,
            ArtistName = album.ArtistName
        };
        _navigationService.Navigate(typeof(AlbumViewPage), navParam);
    }
}
