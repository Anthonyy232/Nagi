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
/// A ViewModel for displaying and managing a list of songs from a specific playlist.
/// </summary>
public partial class PlaylistSongListViewModel : SongListViewModelBase {
    private Guid? _currentPlaylistId;
    private readonly DispatcherTimer _reorderSaveTimer;

    public PlaylistSongListViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService) {
        _reorderSaveTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _reorderSaveTimer.Tick += ReorderSaveTimer_Tick;
    }

    /// <summary>
    /// Gets a value indicating that songs are loaded in pages.
    /// For playlists, we disable paging to allow for full-list reordering.
    /// </summary>
    protected override bool IsPagingSupported => false;

    /// <summary>
    /// Gets a value indicating that the data loaded from the service is already in the
    /// correct, final order. This prevents the base ViewModel from applying a default sort.
    /// THIS IS THE CRITICAL FIX.
    /// </summary>
    protected override bool IsDataPreSortedAfterLoad => true;

    /// <summary>
    /// Gets a flag indicating if the current view represents a real playlist,
    /// which enables playlist-specific actions like song removal and reordering.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    private bool _isCurrentViewAPlaylist;

    /// <summary>
    /// Initializes the ViewModel with the playlist's title and ID, and loads its songs.
    /// </summary>
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
            Debug.WriteLine($"[ERROR] Failed to initialize PlaylistSongListViewModel. {ex.Message}");
            TotalItemsText = "Error loading playlist";
            Songs.Clear();
        }
    }

    /// <summary>
    /// Loads all songs for the current playlist, respecting the saved order.
    /// </summary>
    protected override async Task<IEnumerable<Song>> LoadSongsAsync() {
        if (!_currentPlaylistId.HasValue) {
            return Enumerable.Empty<Song>();
        }
        return await _libraryService.GetSongsInPlaylistOrderedAsync(_currentPlaylistId.Value);
    }

    // These methods are not used because IsPagingSupported is false.
    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) => Task.FromResult(new PagedResult<Song>());
    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) => Task.FromResult(new List<Guid>());

    /// <summary>
    /// Removes the selected songs from the current playlist.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private async Task RemoveSelectedSongsFromPlaylistAsync() {
        if (!_currentPlaylistId.HasValue || !SelectedSongs.Any()) return;

        var songIdsToRemove = SelectedSongs.Select(s => s.Id).ToList();
        Songs.CollectionChanged -= OnSongsCollectionChanged;
        try {
            var success = await _libraryService.RemoveSongsFromPlaylistAsync(_currentPlaylistId.Value, songIdsToRemove);
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

    [RelayCommand]
    private async Task UpdatePlaylistOrderAsync() {
        if (!_currentPlaylistId.HasValue || Songs.Count == 0) return;

        var orderedSongIds = Songs.Select(s => s.Id).ToList();
        await _libraryService.UpdatePlaylistSongOrderAsync(_currentPlaylistId.Value, orderedSongIds);
    }
}