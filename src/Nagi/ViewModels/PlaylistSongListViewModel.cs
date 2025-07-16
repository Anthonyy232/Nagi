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
/// Provides data and commands for displaying and managing the songs within a specific playlist.
/// Supports reordering of songs via drag-and-drop.
/// </summary>
public partial class PlaylistSongListViewModel : SongListViewModelBase {
    private Guid? _currentPlaylistId;
    private readonly DispatcherTimer _reorderSaveTimer;

    public PlaylistSongListViewModel(ILibraryReader libraryReader, IPlaylistService playlistService,
        IMusicPlaybackService playbackService, INavigationService navigationService)
        : base(libraryReader, playlistService, playbackService, navigationService) {
        // A timer to debounce saving the new song order after a drag-drop operation.
        _reorderSaveTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _reorderSaveTimer.Tick += ReorderSaveTimer_Tick;
    }

    protected override bool IsPagingSupported => false;

    protected override bool IsDataPreSortedAfterLoad => true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    private bool _isCurrentViewAPlaylist;

    /// <summary>
    /// Initializes the view model with the details of a specific playlist.
    /// </summary>
    /// <param name="title">The title of the playlist to display.</param>
    /// <param name="playlistId">The unique identifier of the playlist.</param>
    public async Task InitializeAsync(string title, Guid? playlistId) {
        if (IsOverallLoading) return;

        Songs.CollectionChanged -= OnSongsCollectionChanged;

        try {
            PageTitle = title;
            _currentPlaylistId = playlistId;
            IsCurrentViewAPlaylist = playlistId.HasValue;

            await RefreshOrSortSongsCommand.ExecuteAsync(null);

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

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) => Task.FromResult(new PagedResult<Song>());
    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) => Task.FromResult(new List<Guid>());

    /// <summary>
    /// Removes the currently selected songs from the current playlist.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private async Task RemoveSelectedSongsFromPlaylistAsync() {
        if (!_currentPlaylistId.HasValue || !SelectedSongs.Any()) return;

        var songIdsToRemove = SelectedSongs.Select(s => s.Id).ToList();

        // Unsubscribe to prevent reorder logic from firing during removal.
        Songs.CollectionChanged -= OnSongsCollectionChanged;
        try {
            var success = await _playlistService.RemoveSongsFromPlaylistAsync(_currentPlaylistId.Value, songIdsToRemove);
            if (success) {
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

    private void OnSongsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        // When songs are moved, added, or removed, start the timer to save the new order.
        if (e.Action is NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove) {
            _reorderSaveTimer.Start();
        }
    }

    private void ReorderSaveTimer_Tick(object? sender, object e) {
        _reorderSaveTimer.Stop();
        if (UpdatePlaylistOrderCommand.CanExecute(null)) {
            UpdatePlaylistOrderCommand.Execute(null);
        }
    }

    /// <summary>
    /// Saves the current order of songs in the playlist to the database.
    /// </summary>
    [RelayCommand]
    private async Task UpdatePlaylistOrderAsync() {
        if (!_currentPlaylistId.HasValue || Songs.Count == 0) return;

        var orderedSongIds = Songs.Select(s => s.Id).ToList();
        await _playlistService.UpdatePlaylistSongOrderAsync(_currentPlaylistId.Value, orderedSongIds);
    }
}