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
///     A ViewModel for displaying and managing a list of songs from a specific playlist.
/// </summary>
public partial class PlaylistSongListViewModel : SongListViewModelBase {
    private Guid? _currentPlaylistId;

    public PlaylistSongListViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService) {
        CurrentSortOrder = SongSortOrder.TitleAsc;
    }

    /// <summary>
    ///     Overrides the base property to indicate that songs are loaded in pages.
    /// </summary>
    protected override bool IsPagingSupported => true;

    /// <summary>
    ///     Gets a flag indicating if the current view is a real playlist,
    ///     which enables playlist-specific actions like song removal.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    private bool _isCurrentViewAPlaylist;

    /// <summary>
    ///     Initializes the ViewModel with the playlist's title and ID, and loads its songs.
    /// </summary>
    /// <param name="title">The title of the page.</param>
    /// <param name="playlistId">The ID of the playlist to load songs from.</param>
    public async Task InitializeAsync(string title, Guid? playlistId) {
        if (IsOverallLoading) return;

        // FIX: The IsOverallLoading flag is now managed by the base command.
        // This method now only sets up the parameters and calls the command.
        try {
            PageTitle = title;
            _currentPlaylistId = playlistId;
            IsCurrentViewAPlaylist = playlistId.HasValue;

            // The base command will handle setting IsOverallLoading and loading the songs.
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] Failed to initialize PlaylistSongListViewModel. {ex.Message}");
            TotalItemsText = "Error loading playlist";
            Songs.Clear();
        }
    }

    // This method is required by the base class contract but will not be called
    // because IsPagingSupported is true, causing the base to use the paged methods instead.
    protected override Task<IEnumerable<Song>> LoadSongsAsync() {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    /// <summary>
    ///     Loads a specific page of songs for the current playlist from the library service.
    /// </summary>
    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) {
        if (!_currentPlaylistId.HasValue) return new PagedResult<Song>();

        // The 'sortOrder' parameter is intentionally ignored, as playlists have a fixed, user-defined order.
        return await _libraryService.GetSongsByPlaylistPagedAsync(_currentPlaylistId.Value, pageNumber, pageSize);
    }

    /// <summary>
    ///     Loads the full list of song IDs for the current playlist.
    /// </summary>
    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        if (!_currentPlaylistId.HasValue) return new List<Guid>();

        // The 'sortOrder' parameter is intentionally ignored here as well.
        return await _libraryService.GetAllSongIdsByPlaylistIdAsync(_currentPlaylistId.Value);
    }

    /// <summary>
    ///     Removes the selected songs from the current playlist.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private async Task RemoveSelectedSongsFromPlaylistAsync() {
        if (!_currentPlaylistId.HasValue || !SelectedSongs.Any()) return;

        var songIdsToRemove = SelectedSongs.Select(s => s.Id).ToList();

        var success = await _libraryService.RemoveSongsFromPlaylistAsync(_currentPlaylistId.Value, songIdsToRemove);

        if (success) {
            // Refresh the song list from the data source to reflect the removal.
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        else {
            Debug.WriteLine($"[ERROR] Failed to remove songs from playlist: {_currentPlaylistId.Value}");
        }
    }

    /// <summary>
    ///     Determines if the remove songs command can be executed.
    /// </summary>
    private bool CanRemoveSongs() {
        return IsCurrentViewAPlaylist && HasSelectedSongs;
    }
}