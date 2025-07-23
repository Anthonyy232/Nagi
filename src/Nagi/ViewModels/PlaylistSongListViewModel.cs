using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Nagi.Models;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
/// A specialized song list view model for displaying and managing songs within a single playlist.
/// Supports drag-and-drop reordering.
/// </summary>
public partial class PlaylistSongListViewModel : SongListViewModelBase {
    private Guid? _currentPlaylistId;

    // Debounces save operations during rapid drag-and-drop reordering to prevent excessive database writes.
    private readonly DispatcherTimer _reorderSaveTimer;

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
    }

    // Playlists do not support paging; all songs are loaded at once.
    protected override bool IsPagingSupported => false;
    // Playlist songs are already ordered by their position in the playlist.
    protected override bool IsDataPreSortedAfterLoad => true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    public partial bool IsCurrentViewAPlaylist { get; set; }

    /// <summary>
    /// Initializes the view model for a specific playlist.
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
            if (IsCurrentViewAPlaylist) {
                Songs.CollectionChanged += OnSongsCollectionChanged;
            }
        }
        catch (Exception ex) {
            Debug.WriteLine($"[PlaylistSongListViewModel] ERROR: Failed to initialize. {ex.Message}");
            TotalItemsText = "Error loading playlist";
            Songs.Clear();
        }
    }

    protected override async Task<IEnumerable<Song>> LoadSongsAsync() {
        if (!_currentPlaylistId.HasValue) {
            return Enumerable.Empty<Song>();
        }
        return await _libraryReader.GetSongsInPlaylistOrderedAsync(_currentPlaylistId.Value);
    }

    // Paging is not supported for playlists.
    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) => Task.FromResult(new PagedResult<Song>());
    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) => Task.FromResult(new List<Guid>());

    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private async Task RemoveSelectedSongsFromPlaylistAsync() {
        if (!_currentPlaylistId.HasValue || !SelectedSongs.Any()) return;

        var songIdsToRemove = SelectedSongs.Select(s => s.Id).ToList();
        Debug.WriteLine($"[PlaylistSongListViewModel] INFO: Removing {songIdsToRemove.Count} songs from playlist ID '{_currentPlaylistId.Value}'.");

        // Temporarily unsubscribe to prevent reorder logic from firing during removal.
        Songs.CollectionChanged -= OnSongsCollectionChanged;
        try {
            var success = await _playlistService.RemoveSongsFromPlaylistAsync(_currentPlaylistId.Value, songIdsToRemove);
            if (success) {
                // Reload the list from the database to reflect the changes.
                await RefreshOrSortSongsCommand.ExecuteAsync(null);
            }
        }
        finally {
            if (IsCurrentViewAPlaylist) {
                Songs.CollectionChanged += OnSongsCollectionChanged;
            }
        }
    }

    private bool CanRemoveSongs() => IsCurrentViewAPlaylist && HasSelectedSongs;

    /// <summary>
    /// Listens for user-driven changes to the song collection (like drag-drop) to trigger a debounced save.
    /// </summary>
    private void OnSongsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (e.Action is NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove) {
            // Instead of saving on every micro-change, start a timer. If no more changes occur, the timer's tick will save.
            _reorderSaveTimer.Start();
        }
    }

    /// <summary>
    /// Called by the debouncing timer to execute the save operation.
    /// </summary>
    private void ReorderSaveTimer_Tick(object? sender, object e) {
        _reorderSaveTimer.Stop();
        Debug.WriteLine("[PlaylistSongListViewModel] INFO: Reorder save timer ticked. Executing update.");
        if (UpdatePlaylistOrderCommand.CanExecute(null)) {
            UpdatePlaylistOrderCommand.Execute(null);
        }
    }

    [RelayCommand]
    private async Task UpdatePlaylistOrderAsync() {
        if (!_currentPlaylistId.HasValue || Songs.Count == 0) return;

        var orderedSongIds = Songs.Select(s => s.Id).ToList();
        Debug.WriteLine($"[PlaylistSongListViewModel] INFO: Persisting new song order for playlist ID '{_currentPlaylistId.Value}'.");
        await _playlistService.UpdatePlaylistSongOrderAsync(_currentPlaylistId.Value, orderedSongIds);
    }
}