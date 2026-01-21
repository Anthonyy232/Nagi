using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.Core.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Provides data and commands for the album details page, displaying album art,
///     metadata, and the list of tracks.
/// </summary>
public partial class AlbumViewViewModel : SongListViewModelBase
{
    private readonly IUISettingsService _settingsService;
    private Guid _albumId;
    private int? _albumYear;
    private int _totalSongCount;
    private TimeSpan _totalDuration;
    private CancellationTokenSource? _durationFetchCts;

    public AlbumViewViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<AlbumViewViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, musicNavigationService, dispatcherService, uiService, logger)
    {
        _settingsService = settingsService;
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

    [ObservableProperty] public partial ObservableCollection<object> GroupedSongsFlat { get; set; } = new();

    [ObservableProperty] public partial bool IsGroupedByDisc { get; set; }

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
            var albumTask = _libraryReader.GetAlbumByIdAsync(albumId);
            var sortOrderTask = _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.AlbumViewSortOrderKey);
            
            // Wait for sort order first, as it is required for loading songs
            CurrentSortOrder = await sortOrderTask.ConfigureAwait(false);
            
            // Start loading songs in parallel with album metadata
            var songsTask = RefreshOrSortSongsCommand.ExecuteAsync(null);
            
            var album = await albumTask.ConfigureAwait(false);

            if (album != null)
            {
                _dispatcherService.TryEnqueue(() =>
                {
                    AlbumTitle = album.Title;
                    ArtistName = album.ArtistName;
                    PageTitle = album.Title;
                    _albumYear = album.Year;
                    CoverArtUri = ImageUriHelper.GetUriWithCacheBuster(album.CoverArtUri);
                    RefreshAlbumDetailsText();
                });

                await songsTask.ConfigureAwait(false);
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
        _logger.LogWarning("Album with ID {AlbumId} not found", albumId);
        AlbumTitle = "Album Not Found";
        PageTitle = "Not Found";
        ArtistName = string.Empty;
        CoverArtUri = null;
        Songs.Clear();
        TotalItemsText = "0 songs";
    }

    private void HandleLoadError(Guid albumId, Exception ex)
    {
        _logger.LogError(ex, "Failed to load album with ID {AlbumId}", albumId);
        AlbumTitle = "Error Loading Album";
        PageTitle = "Error";
        ArtistName = string.Empty;
        TotalItemsText = "Error";
        Songs.Clear();
    }

    private void UpdateAlbumDetails(PagedResult<Song> pagedResult)
    {
        if (pagedResult?.Items == null) return;

        _totalSongCount = pagedResult.TotalCount;
        
        // As an optimization, if all songs fit in the first page, we can calculate duration in-memory.
        // This is true for 99% of albums (PageSize is 250).
        if (!pagedResult.HasNextPage)
        {
            // Cancel any pending duration fetch since we have all the data
            _durationFetchCts?.Cancel();
            _durationFetchCts?.Dispose();
            _durationFetchCts = null;
            
            _totalDuration = TimeSpan.FromTicks(pagedResult.Items.Sum(s => s.DurationTicks));
            RefreshAlbumDetailsText();
        }
        else
        {
            // Large album or filtered results spanning multiple pages - fetch total sum from DB.
            // Cancel any previous fetch to avoid race conditions
            _durationFetchCts?.Cancel();
            _durationFetchCts?.Dispose();
            _durationFetchCts = new CancellationTokenSource();
            
            // Capture current search term to avoid stale closures
            var currentSearchTerm = SearchTerm;
            _ = UpdateTotalDurationAsync(currentSearchTerm, _durationFetchCts.Token);
            
            // Show partial duration for now to be responsive
            _totalDuration = TimeSpan.FromTicks(pagedResult.Items.Sum(s => s.DurationTicks));
            RefreshAlbumDetailsText();
        }
    }

    private async Task UpdateTotalDurationAsync(string searchTerm, CancellationToken cancellationToken)
    {
        try
        {
            var duration = await _libraryReader.GetSearchTotalDurationInAlbumAsync(_albumId, searchTerm).ConfigureAwait(false);
            
            if (!cancellationToken.IsCancellationRequested)
            {
                _totalDuration = duration;
                _dispatcherService.TryEnqueue(RefreshAlbumDetailsText);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when user changes search or navigates away
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch total duration for album {AlbumId}", _albumId);
        }
    }

    private void RefreshAlbumDetailsText()
    {
        var detailsParts = new List<string>();
        
        if (_albumYear.HasValue) 
            detailsParts.Add(_albumYear.Value.ToString());
            
        detailsParts.Add($"{_totalSongCount} song{(_totalSongCount != 1 ? "s" : "")}");
        
        if (_totalDuration > TimeSpan.Zero)
        {
            var durationText = _totalDuration.TotalHours >= 1 
                ? _totalDuration.ToString(@"h\:mm\:ss") 
                : _totalDuration.ToString(@"m\:ss");
            detailsParts.Add(durationText);
        }

        AlbumDetailsText = string.Join(" • ", detailsParts);
    }


    protected override void ProcessPagedResult(PagedResult<Song> pagedResult, CancellationToken token, bool append = false)
    {
        base.ProcessPagedResult(pagedResult, token, append);
        
        // Update grouping after the songs collection has been updated
        _dispatcherService.TryEnqueue(() =>
        {
             if (!token.IsCancellationRequested) UpdateGrouping();
        });
    }

    private void UpdateGrouping()
    {
        // Only group by disc when sorting by TrackNumberAsc and not searching
        IsGroupedByDisc = CurrentSortOrder == SongSortOrder.TrackNumberAsc && !IsSearchActive;

        if (IsGroupedByDisc)
        {
            GroupedSongsFlat.Clear();

            // Group songs by disc number (null/0 goes to group -1 to appear first)
            var groups = Songs
                .GroupBy(s => s.DiscNumber ?? -1)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                // Add header only for actual disc numbers (1, 2, 3, etc.)
                if (group.Key > 0)
                {
                    GroupedSongsFlat.Add(new DiscHeader { DiscNumber = group.Key });
                }

                // Add all songs in this disc group
                foreach (var song in group)
                {
                    GroupedSongsFlat.Add(song);
                }
            }
        }
        else
        {
            GroupedSongsFlat.Clear();
        }
    }

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.AlbumViewSortOrderKey, sortOrder);
    }

    public override void Cleanup()
    {
        _durationFetchCts?.Cancel();
        _durationFetchCts?.Dispose();
        _durationFetchCts = null;
        base.Cleanup();
    }
}

/// <summary>
///     Represents a disc section header.
/// </summary>
public class DiscHeader
{
    public int DiscNumber { get; set; }
    public string Title => $"Disc {DiscNumber}";
}