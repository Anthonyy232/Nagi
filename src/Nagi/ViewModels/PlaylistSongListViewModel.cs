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
///     A ViewModel for displaying and managing a list of songs from a specific playlist.
/// </summary>
public partial class PlaylistSongListViewModel : SongListViewModelBase
{
    private readonly DispatcherTimer _reorderSaveTimer;

    public PlaylistSongListViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService)
    {
        _reorderSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100) // ~49 ms between remove-add notification
        };
        _reorderSaveTimer.Tick += ReorderSaveTimer_Tick;
    }

    /// <summary>
    ///     Overrides the base property to indicate that songs loaded for a playlist
    ///     are already sorted by the database, preventing an unnecessary in-memory sort.
    /// </summary>
    protected override bool IsDataPreSortedAfterLoad => true;

    /// <summary>
    ///     Gets or sets the ID of the playlist currently being displayed.
    /// </summary>
    [ObservableProperty]
    public partial Guid? CurrentPlaylistId { get; set; }

    /// <summary>
    ///     Gets or sets a flag indicating if the current view is a real playlist,
    ///     which enables playlist-specific actions like reordering and song removal.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    public partial bool IsCurrentViewAPlaylist { get; set; }

    /// <summary>
    ///     Initializes the ViewModel with the playlist's title and ID, and loads its songs.
    /// </summary>
    /// <param name="title">The title of the page.</param>
    /// <param name="playlistId">The ID of the playlist to load songs from.</param>
    public async Task InitializeAsync(string title, Guid? playlistId)
    {
        PageTitle = title;
        CurrentPlaylistId = playlistId;
        IsCurrentViewAPlaylist = playlistId.HasValue;

        // Ensure any previous event handler is detached before refreshing the song list.
        Songs.CollectionChanged -= OnSongsCollectionChanged;

        await RefreshOrSortSongsCommand.ExecuteAsync(null);

        // Subscribe to collection changes only for actual playlists to handle reordering.
        if (IsCurrentViewAPlaylist) Songs.CollectionChanged += OnSongsCollectionChanged;
    }

    /// <summary>
    ///     Loads the songs for the current playlist from the library service.
    /// </summary>
    protected override async Task<IEnumerable<Song>> LoadSongsAsync()
    {
        if (!CurrentPlaylistId.HasValue) return Enumerable.Empty<Song>();

        return await _libraryService.GetSongsInPlaylistOrderedAsync(CurrentPlaylistId.Value);
    }

    /// <summary>
    ///     Handles the CollectionChanged event to save the new song order after a user action.
    /// </summary>
    private void OnSongsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A user-driven reorder (drag-drop) results in a Move, Add, or Remove action.
        // We use a timer to debounce the save operation, preventing saves on every micro-movement.
        if (e.Action is NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Add
            or NotifyCollectionChangedAction.Remove)
        {
            Debug.WriteLine($"Playlist reorder detected ({e.Action}). Debouncing save operation.");
            _reorderSaveTimer.Start();
        }
    }

    /// <summary>
    ///     Executes when the reorder save timer ticks, indicating that reordering has likely completed.
    /// </summary>
    private void ReorderSaveTimer_Tick(object? sender, object e)
    {
        _reorderSaveTimer.Stop();
        Debug.WriteLine("Playlist reorder debounce timer elapsed. Saving new order.");

        if (UpdatePlaylistOrderCommand.CanExecute(null)) UpdatePlaylistOrderCommand.Execute(null);
    }

    /// <summary>
    ///     Saves the current order of songs in the playlist to the database.
    /// </summary>
    [RelayCommand]
    private async Task UpdatePlaylistOrderAsync()
    {
        if (!CurrentPlaylistId.HasValue || Songs.Count == 0) return;

        var orderedSongIds = Songs.Select(s => s.Id).ToList();
        var success = await _libraryService.UpdatePlaylistSongOrderAsync(CurrentPlaylistId.Value, orderedSongIds);

        if (success)
            Debug.WriteLine($"Successfully updated song order for playlist: {CurrentPlaylistId.Value}.");
        else
            // Crucial log for diagnosing save failures.
            Debug.WriteLine($"Failed to update song order for playlist: {CurrentPlaylistId.Value}.");
    }

    /// <summary>
    ///     Removes the selected songs from the current playlist.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private async Task RemoveSelectedSongsFromPlaylistAsync()
    {
        if (!CurrentPlaylistId.HasValue || !SelectedSongs.Any()) return;

        var songIdsToRemove = SelectedSongs.Select(s => s.Id).ToList();

        // Temporarily detach the event handler to prevent it from firing when we
        // programmatically refresh the list after deletion. This avoids an
        // unintended "reorder save" operation.
        Songs.CollectionChanged -= OnSongsCollectionChanged;

        try
        {
            var success = await _libraryService.RemoveSongsFromPlaylistAsync(CurrentPlaylistId.Value, songIdsToRemove);

            if (success)
                // Refresh the song list from the data source to reflect the removal.
                await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        finally
        {
            // Re-attach the handler in a finally block to ensure the UI can
            // respond to subsequent user reordering, even if an error occurred.
            if (IsCurrentViewAPlaylist) Songs.CollectionChanged += OnSongsCollectionChanged;
        }
    }

    /// <summary>
    ///     Determines if the remove songs command can be executed.
    /// </summary>
    private bool CanRemoveSongs()
    {
        return IsCurrentViewAPlaylist && HasSelectedSongs;
    }
}