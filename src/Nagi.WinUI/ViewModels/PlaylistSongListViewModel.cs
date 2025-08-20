using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     A specialized song list view model for displaying and managing songs within a single playlist.
///     Supports drag-and-drop reordering.
/// </summary>
public partial class PlaylistSongListViewModel : SongListViewModelBase {
    private const int SearchDebounceDelay = 400;
    private CancellationTokenSource? _debounceCts;

    // Debounces save operations during rapid drag-and-drop reordering to prevent excessive database writes.
    private readonly DispatcherTimer _reorderSaveTimer;
    private Guid? _currentPlaylistId;

    public PlaylistSongListViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService) {
        _reorderSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _reorderSaveTimer.Tick += ReorderSaveTimer_Tick;
        SearchTerm = string.Empty;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReorderingEnabled))]
    [NotifyPropertyChangedFor(nameof(IsPagingSupported))]
    public partial string SearchTerm { get; set; }

    partial void OnSearchTermChanged(string value) {
        TriggerDebouncedSearch();
    }

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);

    // Paging is only supported when a search is active. Otherwise, all songs are loaded for reordering.
    protected override bool IsPagingSupported => IsSearchActive;

    // Playlist songs are pre-sorted by position when not searching.
    protected override bool IsDataPreSortedAfterLoad => !IsSearchActive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    [NotifyPropertyChangedFor(nameof(IsReorderingEnabled))]
    public partial bool IsCurrentViewAPlaylist { get; set; }

    /// <summary>
    /// Gets a value indicating whether drag-and-drop reordering is enabled.
    /// Reordering is only allowed for actual playlists and when a search is not active.
    /// </summary>
    public bool IsReorderingEnabled => IsCurrentViewAPlaylist && !IsSearchActive;

    /// <summary>
    ///     Initializes the view model for a specific playlist.
    /// </summary>
    public async Task InitializeAsync(string title, Guid? playlistId) {
        if (IsOverallLoading) return;
        Debug.WriteLine($"[PlaylistSongListViewModel] INFO: Initializing for playlist '{title}' (ID: {playlistId}).");

        // Unsubscribe before refresh to prevent the handler from firing on the initial load.
        Songs.CollectionChanged -= OnSongsCollectionChanged;

        try {
            PageTitle = title;
            _currentPlaylistId = playlistId;
            IsCurrentViewAPlaylist = playlistId.HasValue;

            await RefreshOrSortSongsCommand.ExecuteAsync(null);

            // Re-subscribe only if it's a real playlist, enabling reordering logic.
            if (IsCurrentViewAPlaylist) Songs.CollectionChanged += OnSongsCollectionChanged;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[PlaylistSongListViewModel] ERROR: Failed to initialize. {ex.Message}");
            TotalItemsText = "Error loading playlist";
            Songs.Clear();
        }
    }

    protected override async Task<IEnumerable<Song>> LoadSongsAsync() {
        // This method is now only called when IsPagingSupported is false (i.e., not searching).
        if (!_currentPlaylistId.HasValue) return Enumerable.Empty<Song>();
        return await _libraryReader.GetSongsInPlaylistOrderedAsync(_currentPlaylistId.Value);
    }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder) {
        // This method is only called when IsPagingSupported is true (i.e., searching).
        if (!_currentPlaylistId.HasValue || !IsSearchActive) {
            return Task.FromResult(new PagedResult<Song>());
        }
        return _libraryReader.SearchSongsInPlaylistPagedAsync(_currentPlaylistId.Value, SearchTerm, pageNumber, pageSize);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        if (!_currentPlaylistId.HasValue) return Task.FromResult(new List<Guid>());

        if (IsSearchActive) {
            return _libraryReader.SearchAllSongIdsInPlaylistAsync(_currentPlaylistId.Value, SearchTerm);
        }

        // When not searching, get all song IDs in their saved order.
        return _libraryReader.GetAllSongIdsByPlaylistIdAsync(_currentPlaylistId.Value);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private async Task RemoveSelectedSongsFromPlaylistAsync() {
        if (!_currentPlaylistId.HasValue || !SelectedSongs.Any()) return;

        var songIdsToRemove = SelectedSongs.Select(s => s.Id).ToList();
        Debug.WriteLine(
            $"[PlaylistSongListViewModel] INFO: Removing {songIdsToRemove.Count} songs from playlist ID '{_currentPlaylistId.Value}'.");

        // Temporarily unsubscribe to prevent reorder logic from firing during removal.
        Songs.CollectionChanged -= OnSongsCollectionChanged;
        try {
            var success =
                await _playlistService.RemoveSongsFromPlaylistAsync(_currentPlaylistId.Value, songIdsToRemove);
            if (success)
                // Reload the list from the database to reflect the changes.
                await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        finally {
            if (IsCurrentViewAPlaylist) Songs.CollectionChanged += OnSongsCollectionChanged;
        }
    }

    private bool CanRemoveSongs() {
        return IsCurrentViewAPlaylist && HasSelectedSongs;
    }

    /// <summary>
    ///     Listens for user-driven changes to the song collection (like drag-drop) to trigger a debounced save.
    /// </summary>
    private void OnSongsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (e.Action is NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Add
            or NotifyCollectionChangedAction.Remove)
            // Instead of saving on every micro-change, start a timer. If no more changes occur, the timer's tick will save.
            _reorderSaveTimer.Start();
    }

    /// <summary>
    ///     Called by the debouncing timer to execute the save operation.
    /// </summary>
    private void ReorderSaveTimer_Tick(object? sender, object e) {
        _reorderSaveTimer.Stop();
        Debug.WriteLine("[PlaylistSongListViewModel] INFO: Reorder save timer ticked. Executing update.");
        if (UpdatePlaylistOrderCommand.CanExecute(null)) UpdatePlaylistOrderCommand.Execute(null);
    }

    [RelayCommand]
    private async Task UpdatePlaylistOrderAsync() {
        if (!_currentPlaylistId.HasValue || Songs.Count == 0) return;

        var orderedSongIds = Songs.Select(s => s.Id).ToList();
        Debug.WriteLine(
            $"[PlaylistSongListViewModel] INFO: Persisting new song order for playlist ID '{_currentPlaylistId.Value}'.");
        await _playlistService.UpdatePlaylistSongOrderAsync(_currentPlaylistId.Value, orderedSongIds);
    }

    /// <summary>
    /// Executes an immediate search or refresh, cancelling any pending debounced search.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync() {
        _debounceCts?.Cancel();
        await RefreshOrSortSongsCommand.ExecuteAsync(null);
    }

    private void TriggerDebouncedSearch() {
        try {
            _debounceCts?.Cancel();
        }
        catch (ObjectDisposedException) {
            // Ignore exception if the CancellationTokenSource has already been disposed.
        }

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () => {
            try {
                await Task.Delay(SearchDebounceDelay, token);

                if (token.IsCancellationRequested) return;

                await _dispatcherService.EnqueueAsync(async () => {
                    // Re-check the cancellation token after dispatching to prevent a race condition.
                    if (token.IsCancellationRequested) return;
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                });
            }
            catch (TaskCanceledException) {
                Debug.WriteLine("[PlaylistSongListViewModel] Debounced search cancelled.");
            }
            catch (Exception ex) {
                Debug.WriteLine($"[PlaylistSongListViewModel] ERROR: Debounced search failed. {ex.Message}");
            }
        }, token);
    }

    /// <summary>
    ///     Cleans up resources specific to this view model, such as timers and event subscriptions,
    ///     and then calls the base class's cleanup logic.
    /// </summary>
    public override void Cleanup() {
        Debug.WriteLine("[PlaylistSongListViewModel] INFO: Cleaning up resources.");

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        SearchTerm = string.Empty;

        // Stop the timer and unsubscribe to prevent it from firing after disposal
        // and to allow the ViewModel to be garbage collected.
        _reorderSaveTimer.Stop();
        _reorderSaveTimer.Tick -= ReorderSaveTimer_Tick;

        Songs.CollectionChanged -= OnSongsCollectionChanged;
        base.Cleanup();
    }
}