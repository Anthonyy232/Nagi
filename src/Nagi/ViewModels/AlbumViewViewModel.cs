// Nagi/ViewModels/AlbumViewViewModel.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Models;
using Nagi.Services;

namespace Nagi.ViewModels;

/// <summary>
///     Provides data and logic for the AlbumViewPage, which displays details for a single album.
/// </summary>
public partial class AlbumViewViewModel : SongListViewModelBase
{
    private Guid _albumId;

    public AlbumViewViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService)
    {
        // Default sort order for an album is by track number.
        CurrentSortOrder = SongSortOrder.TrackNumberAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    [ObservableProperty] public partial string AlbumTitle { get; set; } = "Album";

    [ObservableProperty] public partial string ArtistName { get; set; } = "Artist";

    [ObservableProperty] public partial string? CoverArtUri { get; set; }

    [ObservableProperty] public partial string AlbumDetailsText { get; set; } = string.Empty;

    /// <summary>
    ///     Asynchronously loads the details for a specific album using its ID.
    /// </summary>
    /// <param name="albumId">The unique identifier of the album to load.</param>
    [RelayCommand]
    public async Task LoadAlbumDetailsAsync(Guid albumId)
    {
        if (IsOverallLoading) return;

        _albumId = albumId;

        try
        {
            var album = await _libraryService.GetAlbumByIdAsync(albumId);
            if (album != null)
            {
                AlbumTitle = album.Title;
                ArtistName = album.Artist?.Name ?? "Unknown Artist";
                PageTitle = album.Title;
                CoverArtUri = album.Songs?.OrderBy(s => s.TrackNumber)
                    .FirstOrDefault(s => !string.IsNullOrEmpty(s.AlbumArtUriFromTrack))
                    ?.AlbumArtUriFromTrack;

                var detailsParts = new List<string>();
                if (album.Year.HasValue) detailsParts.Add(album.Year.Value.ToString());
                var songCount = album.Songs?.Count ?? 0;
                detailsParts.Add($"{songCount} song{(songCount != 1 ? "s" : "")}");

                var totalDuration = TimeSpan.FromSeconds(album.Songs?.Sum(s => s.Duration.TotalSeconds) ?? 0);
                if (totalDuration.TotalMinutes >= 1) detailsParts.Add($"{(int)totalDuration.TotalMinutes} min");

                AlbumDetailsText = string.Join(" • ", detailsParts);

                // This will trigger the base class to load the songs and manage the loading state.
                await RefreshOrSortSongsCommand.ExecuteAsync(null);
            }
            else
            {
                // Handle the case where the album is not found in the library.
                Debug.WriteLine($"Album with ID '{albumId}' not found.");
                AlbumTitle = "Album Not Found";
                PageTitle = "Not Found";
                ArtistName = string.Empty;
                CoverArtUri = null;
                Songs.Clear();
                TotalItemsText = "0 songs";
            }
        }
        catch (Exception ex)
        {
            // Log the error and update the UI to inform the user.
            Debug.WriteLine($"Error loading album with ID '{albumId}': {ex.Message}");
            AlbumTitle = "Error Loading Album";
            PageTitle = "Error";
            ArtistName = string.Empty;
            TotalItemsText = "Error";
            Songs.Clear();
        }
    }

    /// <summary>
    ///     Loads all songs for the current album from the library service.
    ///     This method is called by the base view model's song loading and sorting logic.
    /// </summary>
    /// <returns>A collection of songs belonging to the current album.</returns>
    protected override async Task<IEnumerable<Song>> LoadSongsAsync()
    {
        if (_albumId == Guid.Empty) return Enumerable.Empty<Song>();
        return await _libraryService.GetSongsByAlbumIdAsync(_albumId);
    }
}