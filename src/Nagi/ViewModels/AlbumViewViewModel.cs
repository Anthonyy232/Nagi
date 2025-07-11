using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Models;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
/// ViewModel for the album details page, displaying album art, metadata, and the list of tracks.
/// </summary>
public partial class AlbumViewViewModel : SongListViewModelBase {
    private Guid _albumId;
    private int? _albumYear;

    public AlbumViewViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService) {
        CurrentSortOrder = SongSortOrder.TrackNumberAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    [ObservableProperty]
    private string _albumTitle = "Album";

    [ObservableProperty]
    private string _artistName = "Artist";

    [ObservableProperty]
    private string? _coverArtUri;

    [ObservableProperty]
    private string _albumDetailsText = string.Empty;

    protected override bool IsPagingSupported => true;

    protected override Task<IEnumerable<Song>> LoadSongsAsync() {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    /// <summary>
    /// Loads a page of songs for the current album. On the first page load,
    /// it also updates the album's metadata display text.
    /// </summary>
    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) {
        if (_albumId == Guid.Empty) return new PagedResult<Song>();

        var result = await _libraryService.GetSongsByAlbumIdPagedAsync(_albumId, pageNumber, pageSize, sortOrder);

        if (pageNumber == 1) {
            UpdateAlbumDetails(result);
        }

        return result;
    }

    /// <summary>
    /// Loads the complete list of song IDs for the current album.
    /// </summary>
    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        if (_albumId == Guid.Empty) return new List<Guid>();
        return await _libraryService.GetAllSongIdsByAlbumIdAsync(_albumId, sortOrder);
    }

    /// <summary>
    /// Loads the details for a specific album and then initiates loading its songs.
    /// </summary>
    /// <param name="albumId">The ID of the album to load.</param>
    [RelayCommand]
    public async Task LoadAlbumDetailsAsync(Guid albumId) {
        if (IsOverallLoading) return;

        try {
            _albumId = albumId;
            var album = await _libraryService.GetAlbumByIdAsync(albumId);

            if (album != null) {
                AlbumTitle = album.Title;
                ArtistName = album.Artist?.Name ?? "Unknown Artist";
                PageTitle = album.Title;
                _albumYear = album.Year;
                CoverArtUri = album.CoverArtUri;

                await RefreshOrSortSongsCommand.ExecuteAsync(null);
            }
            else {
                HandleAlbumNotFound(albumId);
            }
        }
        catch (Exception ex) {
            HandleLoadError(albumId, ex);
        }
    }

    private void HandleAlbumNotFound(Guid albumId) {
        Debug.WriteLine($"[AlbumViewViewModel] Album with ID '{albumId}' not found.");
        AlbumTitle = "Album Not Found";
        PageTitle = "Not Found";
        ArtistName = string.Empty;
        CoverArtUri = null;
        Songs.Clear();
        TotalItemsText = "0 songs";
    }

    private void HandleLoadError(Guid albumId, Exception ex) {
        Debug.WriteLine($"[ERROR] Error loading album with ID '{albumId}': {ex.Message}");
        AlbumTitle = "Error Loading Album";
        PageTitle = "Error";
        ArtistName = string.Empty;
        TotalItemsText = "Error";
        Songs.Clear();
    }

    /// <summary>
    /// Constructs and sets the album details string (e.g., "2023 • 12 songs").
    /// </summary>
    private void UpdateAlbumDetails(PagedResult<Song> pagedResult) {
        if (pagedResult?.Items == null) return;

        var songCount = pagedResult.TotalCount;
        var detailsParts = new List<string>();
        if (_albumYear.HasValue) {
            detailsParts.Add(_albumYear.Value.ToString());
        }
        detailsParts.Add($"{songCount} song{(songCount != 1 ? "s" : "")}");

        AlbumDetailsText = string.Join(" • ", detailsParts);
    }
}