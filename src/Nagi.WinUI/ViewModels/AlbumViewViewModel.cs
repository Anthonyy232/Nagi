using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Provides data and commands for the album details page, displaying album art,
///     metadata, and the list of tracks.
/// </summary>
public partial class AlbumViewViewModel : SongListViewModelBase
{
    private const int SearchDebounceDelay = 400;
    private Guid _albumId;
    private int? _albumYear;
    private CancellationTokenSource? _debounceCts;

    public AlbumViewViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService)
    {
        // Initialize properties with default values here, as partial properties don't support initializers.
        AlbumTitle = "Album";
        ArtistName = "Artist";
        AlbumDetailsText = string.Empty;

        CurrentSortOrder = SongSortOrder.TrackNumberAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    [ObservableProperty] public partial string AlbumTitle { get; set; }

    [ObservableProperty] public partial string ArtistName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? CoverArtUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverArtUri);

    [ObservableProperty] public partial string AlbumDetailsText { get; set; }

    [ObservableProperty] public partial string SearchTerm { get; set; }

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);

    protected override bool IsPagingSupported => true;

    partial void OnSearchTermChanged(string value)
    {
        TriggerDebouncedSearch();
    }

    protected override Task<IEnumerable<Song>> LoadSongsAsync()
    {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        if (_albumId == Guid.Empty) return new PagedResult<Song>();

        PagedResult<Song> result;
        if (IsSearchActive)
            result = await _libraryReader.SearchSongsInAlbumPagedAsync(_albumId, SearchTerm, pageNumber, pageSize);
        else
            result = await _libraryReader.GetSongsByAlbumIdPagedAsync(_albumId, pageNumber, pageSize, sortOrder);

        if (pageNumber == 1) UpdateAlbumDetails(result);

        return result;
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        if (_albumId == Guid.Empty) return new List<Guid>();

        if (IsSearchActive) return await _libraryReader.SearchAllSongIdsInAlbumAsync(_albumId, SearchTerm, sortOrder);

        return await _libraryReader.GetAllSongIdsByAlbumIdAsync(_albumId, sortOrder);
    }

    /// <summary>
    ///     Loads the details and track list for a specific album.
    /// </summary>
    /// <param name="albumId">The unique identifier of the album to load.</param>
    [RelayCommand]
    public async Task LoadAlbumDetailsAsync(Guid albumId)
    {
        if (IsOverallLoading) return;

        try
        {
            _albumId = albumId;
            var album = await _libraryReader.GetAlbumByIdAsync(albumId);

            if (album != null)
            {
                AlbumTitle = album.Title;
                ArtistName = album.Artist?.Name ?? "Unknown Artist";
                PageTitle = album.Title;
                _albumYear = album.Year;
                CoverArtUri = album.CoverArtUri;

                await RefreshOrSortSongsCommand.ExecuteAsync(null);
            }
            else
            {
                HandleAlbumNotFound(albumId);
            }
        }
        catch (Exception ex)
        {
            HandleLoadError(albumId, ex);
        }
    }

    private void HandleAlbumNotFound(Guid albumId)
    {
        Debug.WriteLine($"[AlbumViewViewModel] INFO: Album with ID '{albumId}' not found.");
        AlbumTitle = "Album Not Found";
        PageTitle = "Not Found";
        ArtistName = string.Empty;
        CoverArtUri = null;
        Songs.Clear();
        TotalItemsText = "0 songs";
    }

    private void HandleLoadError(Guid albumId, Exception ex)
    {
        Debug.WriteLine($"[AlbumViewViewModel] ERROR: Failed to load album with ID '{albumId}'. {ex.Message}");
        AlbumTitle = "Error Loading Album";
        PageTitle = "Error";
        ArtistName = string.Empty;
        TotalItemsText = "Error";
        Songs.Clear();
    }

    private void UpdateAlbumDetails(PagedResult<Song> pagedResult)
    {
        if (pagedResult?.Items == null) return;

        var songCount = pagedResult.TotalCount;
        var detailsParts = new List<string>();
        if (_albumYear.HasValue) detailsParts.Add(_albumYear.Value.ToString());
        detailsParts.Add($"{songCount} song{(songCount != 1 ? "s" : "")}");

        AlbumDetailsText = string.Join(" • ", detailsParts);
    }

    /// <summary>
    ///     Executes an immediate search or refresh, cancelling any pending debounced search.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        _debounceCts?.Cancel();
        await RefreshOrSortSongsCommand.ExecuteAsync(null);
    }

    private void TriggerDebouncedSearch()
    {
        try
        {
            _debounceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore exception if the CancellationTokenSource has already been disposed.
        }

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, token);

                if (token.IsCancellationRequested) return;

                await _dispatcherService.EnqueueAsync(async () =>
                {
                    // Re-check the cancellation token after dispatching to prevent a race condition.
                    if (token.IsCancellationRequested) return;
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                });
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine("[AlbumViewViewModel] Debounced search cancelled.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AlbumViewViewModel] ERROR: Debounced search failed. {ex.Message}");
            }
        }, token);
    }

    public override void Cleanup()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        SearchTerm = string.Empty;

        base.Cleanup();
    }
}