using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Models;
using Nagi.Services;

namespace Nagi.ViewModels;

/// <summary>
///     ViewModel for displaying and managing a list of songs from a specific playlist.
/// </summary>
public partial class PlaylistSongListViewModel : SongListViewModelBase
{
    public PlaylistSongListViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService)
    {
    }

    /// <summary>
    ///     The ID of the playlist currently being displayed.
    /// </summary>
    [ObservableProperty]
    public partial Guid? CurrentPlaylistId { get; set; }

    /// <summary>
    ///     A flag indicating if the current view is a real playlist, which enables playlist-specific actions.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSongsFromPlaylistCommand))]
    public partial bool IsCurrentViewAPlaylist { get; set; }

    /// <summary>
    ///     Initializes the ViewModel with the playlist's title and ID.
    /// </summary>
    /// <param name="title">The title of the page.</param>
    /// <param name="playlistId">The ID of the playlist to load songs from.</param>
    public async Task InitializeAsync(string title, Guid? playlistId)
    {
        PageTitle = title;
        CurrentPlaylistId = playlistId;
        IsCurrentViewAPlaylist = playlistId.HasValue;
        await RefreshOrSortSongsCommand.ExecuteAsync(null);
    }

    /// <summary>
    ///     Loads the songs for the current playlist from the library service.
    /// </summary>
    /// <returns>A collection of songs for the current playlist.</returns>
    protected override async Task<IEnumerable<Song>> LoadSongsAsync()
    {
        if (!CurrentPlaylistId.HasValue) return Enumerable.Empty<Song>();
        return await _libraryService.GetSongsInPlaylistOrderedAsync(CurrentPlaylistId.Value);
    }

    /// <summary>
    ///     Command to remove the selected songs from the current playlist.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSongs))]
    private async Task RemoveSelectedSongsFromPlaylistAsync()
    {
        if (!CurrentPlaylistId.HasValue || !SelectedSongs.Any()) return;

        var songIdsToRemove = SelectedSongs.Select(s => s.Id).ToList();
        var success = await _libraryService.RemoveSongsFromPlaylistAsync(CurrentPlaylistId.Value, songIdsToRemove);

        if (success)
            // Refresh the song list to reflect the removal.
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
    }

    /// <summary>
    ///     Determines if the remove songs command can be executed.
    /// </summary>
    private bool CanRemoveSongs()
    {
        return IsCurrentViewAPlaylist && HasSelectedSongs;
    }
}