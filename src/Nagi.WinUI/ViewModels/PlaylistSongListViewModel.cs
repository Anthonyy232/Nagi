using System;
using System.Collections.Generic;
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

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     A specialized song list view model for displaying and managing songs within a single playlist.
/// </summary>
public partial class PlaylistSongListViewModel : SongListViewModelBase
{
    private const int SearchDebounceDelay = 400;

    private Guid? _currentPlaylistId;
    private CancellationTokenSource? _debounceCts;

    public PlaylistSongListViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger<PlaylistSongListViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService, logger)
    {
    }

    [ObservableProperty]
    public partial string SearchTerm { get; set; } = string.Empty;

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);

    // Always use paging for efficient loading of large playlists.
    protected override bool IsPagingSupported => true;


    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    public partial bool IsCurrentViewAPlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? CoverImageUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverImageUri);

    partial void OnSearchTermChanged(string value)
    {
        TriggerDebouncedSearch();
    }

    /// <summary>
    ///     Initializes the view model for a specific playlist.
    /// </summary>
    public async Task InitializeAsync(string title, Guid? playlistId, string? coverImageUri = null)
    {
        CurrentSortOrder = SongSortOrder.TitleAsc;
        if (IsOverallLoading) return;
        _logger.LogDebug("Initializing for playlist '{Title}' (ID: {PlaylistId})", title, playlistId);

        try
        {
            PageTitle = title;
            _currentPlaylistId = playlistId;
            IsCurrentViewAPlaylist = playlistId.HasValue;
            CoverImageUri = coverImageUri;

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize playlist {PlaylistId}", _currentPlaylistId);
            TotalItemsText = "Error loading playlist";
            Songs.Clear();
        }
    }

    protected override Task<IEnumerable<Song>> LoadSongsAsync()
    {
        // Not used since IsPagingSupported is always true.
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        if (!_currentPlaylistId.HasValue) return Task.FromResult(new PagedResult<Song>());

        if (IsSearchActive)
            return _libraryReader.SearchSongsInPlaylistPagedAsync(_currentPlaylistId.Value, SearchTerm, pageNumber, pageSize, sortOrder);

        return _libraryReader.GetSongsByPlaylistPagedAsync(_currentPlaylistId.Value, pageNumber, pageSize, sortOrder);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        if (!_currentPlaylistId.HasValue) return Task.FromResult(new List<Guid>());

        if (IsSearchActive)
            return _libraryReader.SearchAllSongIdsInPlaylistAsync(_currentPlaylistId.Value, SearchTerm, sortOrder);

        // When not searching, get all song IDs in the requested order.
        return _libraryReader.GetAllSongIdsByPlaylistIdAsync(_currentPlaylistId.Value, sortOrder);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private async Task RemoveSelectedSongsFromPlaylistAsync()
    {
        if (!_currentPlaylistId.HasValue || !SelectedSongs.Any()) return;

        var songIdsToRemove = SelectedSongs.Select(s => s.Id).ToList();
        _logger.LogDebug("Removing {SongCount} songs from playlist ID {PlaylistId}", songIdsToRemove.Count,
            _currentPlaylistId.Value);

        var success =
            await _playlistService.RemoveSongsFromPlaylistAsync(_currentPlaylistId.Value, songIdsToRemove);
        if (success)
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
    }

    private bool CanRemoveSongs()
    {
        return IsCurrentViewAPlaylist && HasSelectedSongs;
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
            _debounceCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("CancellationTokenSource was already disposed during search cancellation");
        }

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
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
                _logger.LogDebug("Debounced search cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Debounced search failed for playlist {PlaylistId}", _currentPlaylistId);
            }
        }, token);
    }

    /// <summary>
    ///     Cleans up resources specific to this view model.
    /// </summary>
    public override void Cleanup()
    {
        _logger.LogDebug("Cleaning up PlaylistSongListViewModel resources");

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
        _currentPlaylistId = null;
        SearchTerm = string.Empty;

        base.Cleanup();
    }
}