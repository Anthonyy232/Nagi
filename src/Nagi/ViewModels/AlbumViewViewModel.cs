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
    private string albumTitle = "Album";

    [ObservableProperty]
    private string artistName = "Artist";

    [ObservableProperty]
    private string? coverArtUri;

    [ObservableProperty]
    private string albumDetailsText = string.Empty;

    protected override bool IsPagingSupported => true;

    protected override Task<IEnumerable<Song>> LoadSongsAsync() {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) {
        if (_albumId == Guid.Empty) {
            return new PagedResult<Song>();
        }

        var result = await _libraryService.GetSongsByAlbumIdPagedAsync(_albumId, pageNumber, pageSize, sortOrder);

        if (pageNumber == 1) {
            UpdateAlbumDetails(result);
        }

        return result;
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        if (_albumId == Guid.Empty) {
            return new List<Guid>();
        }
        return await _libraryService.GetAllSongIdsByAlbumIdAsync(_albumId, sortOrder);
    }

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

                try {
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                }
                catch (Exception ex) {
                    Debug.WriteLine($"[ERROR] Failed to load songs for album '{album.Title}'. {ex.Message}");
                    TotalItemsText = "Error loading songs";
                }
            }
            else {
                Debug.WriteLine($"Album with ID '{albumId}' not found.");
                AlbumTitle = "Album Not Found";
                PageTitle = "Not Found";
                ArtistName = string.Empty;
                CoverArtUri = null;
                Songs.Clear();
                TotalItemsText = "0 songs";
            }
        }
        catch (Exception ex) {
            Debug.WriteLine($"Error loading album with ID '{albumId}': {ex.Message}");
            AlbumTitle = "Error Loading Album";
            PageTitle = "Error";
            ArtistName = string.Empty;
            TotalItemsText = "Error";
            Songs.Clear();
        }
    }

    private void UpdateAlbumDetails(PagedResult<Song> pagedResult) {
        if (pagedResult.Items == null) return;

        var songCount = pagedResult.TotalCount;
        var detailsParts = new List<string>();
        if (_albumYear.HasValue) detailsParts.Add(_albumYear.Value.ToString());
        detailsParts.Add($"{songCount} song{(songCount != 1 ? "s" : "")}");

        AlbumDetailsText = string.Join(" • ", detailsParts);
    }
}