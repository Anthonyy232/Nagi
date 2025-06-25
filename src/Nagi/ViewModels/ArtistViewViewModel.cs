// Nagi/ViewModels/ArtistViewViewModel.cs

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Models;
using Nagi.Navigation;
using Nagi.Pages;
using Nagi.Services;

namespace Nagi.ViewModels;

/// <summary>
///     Represents a simplified album model for display within the artist view.
/// </summary>
public partial class ArtistAlbumViewModelItem : ObservableObject
{
    public ArtistAlbumViewModelItem(Album album)
    {
        Id = album.Id;
        Name = album.Title;
        YearText = album.Year?.ToString() ?? string.Empty;
        CoverArtUri = album.Songs?.OrderBy(s => s.TrackNumber)
            .FirstOrDefault(s => !string.IsNullOrEmpty(s.AlbumArtUriFromTrack))
            ?.AlbumArtUriFromTrack;
    }

    [ObservableProperty] public partial Guid Id { get; set; }

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial string? CoverArtUri { get; set; }

    [ObservableProperty] public partial string YearText { get; set; } = string.Empty;
}

/// <summary>
///     Provides data and logic for the ArtistViewPage, managing artist details, albums, and songs.
/// </summary>
public partial class ArtistViewViewModel : SongListViewModelBase
{
    private Guid _artistId;

    public ArtistViewViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService)
    {
        // Default sort order for an artist's song list is by album.
        CurrentSortOrder = SongSortOrder.AlbumAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);

        // Update HasAlbums when Albums collection changes
        Albums.CollectionChanged += (s, e) => HasAlbums = Albums.Any();
    }

    [ObservableProperty] public partial string ArtistName { get; set; } = "Artist";

    [ObservableProperty] public partial string ArtistBio { get; set; } = "No biography available for this artist.";

    [ObservableProperty] public partial ObservableCollection<ArtistAlbumViewModelItem> Albums { get; set; } = new();

    [ObservableProperty] public partial bool HasAlbums { get; set; }

    /// <summary>
    ///     Asynchronously loads the details for a specific artist, including their albums and songs.
    /// </summary>
    /// <param name="artistId">The unique identifier of the artist to load.</param>
    [RelayCommand]
    public async Task LoadArtistDetailsAsync(Guid artistId)
    {
        if (IsOverallLoading) return;

        _artistId = artistId;

        try
        {
            var artist = await _libraryService.GetArtistByIdAsync(artistId);
            if (artist != null)
            {
                ArtistName = artist.Name;
                PageTitle = artist.Name;
                var albumsFromDb = artist.Albums.OrderByDescending(a => a.Year).ThenBy(a => a.Title);
                Albums = new ObservableCollection<ArtistAlbumViewModelItem>(
                    albumsFromDb.Select(a => new ArtistAlbumViewModelItem(a)));
                HasAlbums = Albums.Any();

                // This will trigger the base class to load the songs and manage the loading state.
                await RefreshOrSortSongsCommand.ExecuteAsync(null);
            }
            else
            {
                // Handle the case where the artist is not found in the library.
                Debug.WriteLine($"Artist with ID '{artistId}' not found.");
                ArtistName = "Artist Not Found";
                PageTitle = "Not Found";
                Albums.Clear();
                Songs.Clear();
                TotalItemsText = "0 songs";
                HasAlbums = false;
            }
        }
        catch (Exception ex)
        {
            // Log the error and update the UI to inform the user.
            Debug.WriteLine($"Error loading artist with ID '{artistId}': {ex.Message}");
            ArtistName = "Error Loading Artist";
            PageTitle = "Error";
            TotalItemsText = "Error";
            Albums.Clear();
            Songs.Clear();
            HasAlbums = false;
        }
    }

    /// <summary>
    ///     Navigates to the album detail page for the selected album.
    /// </summary>
    /// <param name="albumId">The ID of the album to navigate to.</param>
    [RelayCommand]
    private void ViewAlbum(Guid albumId)
    {
        if (albumId != Guid.Empty)
            _navigationService.Navigate(
                typeof(AlbumViewPage),
                new AlbumViewNavigationParameter { AlbumId = albumId });
    }

    /// <summary>
    ///     Loads all songs for the current artist from the library service.
    ///     This method is called by the base view model's song loading and sorting logic.
    /// </summary>
    /// <returns>A collection of songs by the current artist.</returns>
    protected override async Task<IEnumerable<Song>> LoadSongsAsync()
    {
        if (_artistId == Guid.Empty) return Enumerable.Empty<Song>();
        return await _libraryService.GetSongsByArtistIdAsync(_artistId);
    }
}