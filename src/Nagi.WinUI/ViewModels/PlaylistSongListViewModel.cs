using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     A specialized song list view model for displaying and managing songs within a single playlist.
/// </summary>
public partial class PlaylistSongListViewModel : SongListViewModelBase
{
    private Guid? _currentPlaylistId;
    private CancellationTokenSource? _saveOrderCts;

    public PlaylistSongListViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger<PlaylistSongListViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, musicNavigationService, dispatcherService, uiService, logger)
    {
        // Subscribe to the initial collection.
        if (Songs != null) Songs.CollectionChanged += OnSongsCollectionChanged;
    }


    public bool IsReorderingEnabled => IsCurrentViewAPlaylist && CurrentSortOrder == SongSortOrder.PlaylistOrder && !IsSearchActive;



    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    public partial bool IsCurrentViewAPlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? CoverImageUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverImageUri);

    protected override void OnSearchTermChangedInternal(string value)
    {
        base.OnSearchTermChangedInternal(value);
        OnPropertyChanged(nameof(IsReorderingEnabled));
    }

    protected override void OnCurrentSortOrderChangedInternal(SongSortOrder oldOrder, SongSortOrder newOrder)
    {
        base.OnCurrentSortOrderChangedInternal(oldOrder, newOrder);
        OnPropertyChanged(nameof(IsReorderingEnabled));
    }

    /// <summary>
    ///     Initializes the view model for a specific playlist.
    /// </summary>
    public async Task InitializeAsync(string title, Guid? playlistId, string? coverImageUri = null)
    {
        CurrentSortOrder = SongSortOrder.PlaylistOrder;
        _logger.LogDebug("Initializing for playlist '{Title}' (ID: {PlaylistId})", title, playlistId);

        try
        {
            // Cancel any pending debounced tasks from previous views
            _saveOrderCts?.Cancel();
            CancelPendingSearch();

            PageTitle = title;
            _stateLock.EnterWriteLock();
            try
            {
                _currentPlaylistId = playlistId;
            }
            finally
            {
                _stateLock.ExitWriteLock();
            }
            IsCurrentViewAPlaylist = playlistId.HasValue;
            CoverImageUri = coverImageUri;

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
            OnPropertyChanged(nameof(IsReorderingEnabled));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize playlist {PlaylistId}", _currentPlaylistId);
            TotalItemsText = Nagi.WinUI.Resources.Strings.Playlist_ErrorLoading;
            Songs.Clear();
        }
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

    /// <summary>
    ///     Override to manage the CollectionChanged subscription when the Songs collection is replaced in the base class.
    /// </summary>
    protected override void OnSongsChangedInternal(ObservableRangeCollection<Song> oldValue, ObservableRangeCollection<Song> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= OnSongsCollectionChanged;
        if (newValue != null) newValue.CollectionChanged += OnSongsCollectionChanged;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private async Task RemoveSelectedSongsFromPlaylistAsync()
    {
        if (!_currentPlaylistId.HasValue || !HasSelectedSongs) return;

        var songIdsToRemove = await GetCurrentSelectionIdsAsync();
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

    protected override async Task ExecuteSearchAsync(CancellationToken token)
    {
        var targetPlaylistId = _currentPlaylistId;
        await _dispatcherService.EnqueueAsync(async () =>
        {
            // Re-check context before execution
            _stateLock.EnterReadLock();
            try
            {
                if (token.IsCancellationRequested || _currentPlaylistId != targetPlaylistId) return;
            }
            finally
            {
                _stateLock.ExitReadLock();
            }
            await RefreshOrSortSongsAsync(null, token);
        });
    }

    /// <summary>
    ///     Called from the View when a song has been reordered via drag-and-drop.
    ///     This is the proper way to handle WinUI ListView reordering, as WinUI fires
    ///     Remove+Add collection events, NOT Move events.
    /// </summary>
    public async Task OnSongReorderedAsync(Song movedSong, int newIndex)
    {
        try
        {
            // Capture the playlist ID immediately to prevent race conditions
            Guid playlistId;
            _stateLock.EnterReadLock();
            try
            {
                if (!_currentPlaylistId.HasValue)
                {
                    _logger.LogWarning("Reorder ignored: No playlist ID set.");
                    return;
                }
                playlistId = _currentPlaylistId.Value;
            }
            finally
            {
                _stateLock.ExitReadLock();
            }

            if (!IsReorderingEnabled)
            {
                _logger.LogDebug("Reorder ignored: Reordering is not enabled.");
                return;
            }

            // Defensive bounds check - the collection could have been modified between drag and this handler
            var currentCount = Songs.Count;
            if (newIndex < 0 || newIndex >= currentCount)
            {
                _logger.LogWarning("Reorder ignored: Index {Index} is out of bounds for collection of size {Count}.", newIndex, currentCount);
                return;
            }

            // Calculate the new order based on neighbors
            double prevOrder = double.NegativeInfinity;
            double nextOrder = double.PositiveInfinity;

            if (newIndex > 0)
            {
                var prevSong = Songs[newIndex - 1];
                if (prevSong.Id != movedSong.Id)
                    prevOrder = prevSong.Order;
            }

            if (newIndex < currentCount - 1)
            {
                var nextSong = Songs[newIndex + 1];
                if (nextSong.Id != movedSong.Id)
                    nextOrder = nextSong.Order;
            }

            _logger.LogDebug("Reorder: Song {SongId} at index {Index}, Neighbors: {PrevOrder} | {NextOrder}",
                movedSong.Id, newIndex, prevOrder, nextOrder);

            // Calculate the new fractional order
            double newOrder;
            if (double.IsNegativeInfinity(prevOrder))
            {
                // Moving to the beginning
                if (double.IsPositiveInfinity(nextOrder))
                    newOrder = 1.0; // Only song
                else if (nextOrder > 0.5)
                    newOrder = nextOrder / 2.0;
                else
                    newOrder = nextOrder - 1.0;
            }
            else if (double.IsPositiveInfinity(nextOrder))
            {
                // Moving to the end
                newOrder = Math.Floor(prevOrder) + 1.0;
            }
            else
            {
                // Moving between two songs
                newOrder = (prevOrder + nextOrder) / 2.0;
            }

            if (double.IsNaN(newOrder) || double.IsInfinity(newOrder))
            {
                _logger.LogWarning("Calculated invalid order {Order} for song {SongId}. Falling back to 1.0", newOrder, movedSong.Id);
                newOrder = 1.0;
            }

            // Update the song's order in memory
            movedSong.Order = newOrder;

            // Update the master ID list for playback consistency
            _stateLock.EnterWriteLock();
            try
            {
                var oldIndex = _fullSongIdList.IndexOf(movedSong.Id);
                if (oldIndex >= 0 && oldIndex != newIndex)
                {
                    _fullSongIdList.RemoveAt(oldIndex);
                    var insertIndex = Math.Min(newIndex, _fullSongIdList.Count);
                    _fullSongIdList.Insert(insertIndex, movedSong.Id);
                }
            }
            finally
            {
                _stateLock.ExitWriteLock();
            }

            // Save to database with proper error handling
            _logger.LogDebug("Saving order {Order} for song {SongId} in playlist {PlaylistId}",
                newOrder, movedSong.Id, playlistId);

            var success = await _playlistService.MovePlaylistSongAsync(playlistId, movedSong.Id, newOrder);
            if (!success)
            {
                _logger.LogWarning("MovePlaylistSongAsync returned false for song {SongId} in playlist {PlaylistId}",
                    movedSong.Id, playlistId);
            }

            // Check for precision exhaustion and trigger normalization if needed
            // Using 1e-9 threshold which is more conservative than double precision limits
            if (!double.IsInfinity(prevOrder) && !double.IsInfinity(nextOrder) && Math.Abs(nextOrder - prevOrder) < 1e-9)
            {
                _logger.LogInformation("Precision threshold reached, triggering order normalization for playlist {PlaylistId}", playlistId);
                TriggerDebouncedSaveOrder();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during song reorder for {SongId}", movedSong?.Id);
        }
    }

    private void OnSongsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // This handler now only manages the _fullSongIdList for Remove operations.
        // Reordering is handled by OnSongReordered via DragItemsCompleted event.
        if (!IsReorderingEnabled || IsOverallLoading || IsBackgroundPageLoading) return;

        if (e.Action == NotifyCollectionChangedAction.Remove)
        {
            _stateLock.EnterWriteLock();
            try
            {
                if (e.OldItems != null)
                {
                    foreach (var item in e.OldItems)
                    {
                        if (item is Song song) _fullSongIdList.Remove(song.Id);
                    }
                }
            }
            finally
            {
                _stateLock.ExitWriteLock();
            }
        }
    }

    private void TriggerDebouncedSaveOrder()
    {
        _saveOrderCts?.Cancel();
        _saveOrderCts?.Dispose();
        _saveOrderCts = new CancellationTokenSource();
        var token = _saveOrderCts.Token;

        // Capture the playlist ID that this save operation targets to prevent cross-playlist corruption
        // if the user navigates during the delay.
        var targetPlaylistId = _currentPlaylistId;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait 2 seconds before saving to batch multiple moves during a drag.
                await Task.Delay(2000, token);
                if (token.IsCancellationRequested) return;

                List<Guid> songIds;

                _stateLock.EnterReadLock();
                try
                {
                    // Verify we are still on the same playlist.
                    if (!targetPlaylistId.HasValue || _currentPlaylistId != targetPlaylistId)
                    {
                        _logger.LogDebug("Playlist order normalization aborted: Active playlist changed.");
                        return;
                    }
                    songIds = _fullSongIdList.ToList();
                }
                finally
                {
                    _stateLock.ExitReadLock();
                }

                _logger.LogInformation("Performing full playlist order normalization for {PlaylistId}", targetPlaylistId);
                await _playlistService.UpdatePlaylistOrderAsync(targetPlaylistId.Value, songIds);
            }
            catch (TaskCanceledException) { }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Playlist order normalization stopped because the view model was disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to normalize playlist order for {PlaylistId}", targetPlaylistId);
            }
        }, token);
    }

    /// <summary>
    ///     Cleans up resources specific to this view model.
    /// </summary>
    public override void ResetState()
    {
        _logger.LogDebug("Cleaning up PlaylistSongListViewModel resources");

        if (Songs != null)
        {
            Songs.CollectionChanged -= OnSongsCollectionChanged;
        }

        _saveOrderCts?.Cancel();
        _saveOrderCts?.Dispose();
        _saveOrderCts = null;

        _currentPlaylistId = null;

        base.ResetState();
    }
}