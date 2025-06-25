// Nagi/ViewModels/SongListViewModelBase.cs

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Models;
using Nagi.Navigation;
using Nagi.Pages;
using Nagi.Services;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
///     A base view model for pages that display a list of songs, providing common functionality
///     for loading, sorting, playback, and selection.
/// </summary>
public abstract partial class SongListViewModelBase : ObservableObject
{
    protected readonly ILibraryService _libraryService;
    protected readonly INavigationService _navigationService;
    protected readonly IMusicPlaybackService _playbackService;

    protected SongListViewModelBase(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));

        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    [ObservableProperty] public partial string PageTitle { get; set; } = "Songs";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshOrSortSongsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayAllSongsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShuffleAndPlayAllSongsCommand))]
    public partial bool IsOverallLoading { get; set; }

    [ObservableProperty] public partial ObservableCollection<Song> Songs { get; set; } = new();

    [ObservableProperty] public partial string TotalItemsText { get; set; } = "0 items";

    [ObservableProperty] public partial SongSortOrder CurrentSortOrder { get; set; } = SongSortOrder.TitleAsc;

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = "Sort By: A to Z";

    [ObservableProperty] public partial ObservableCollection<Song> SelectedSongs { get; set; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowInFileExplorerCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToAlbumCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToArtistCommand))]
    public partial bool IsSingleSongSelected { get; set; }

    [ObservableProperty] public partial ObservableCollection<Playlist> AvailablePlaylists { get; set; } = new();

    public bool HasSelectedSongs => SelectedSongs.Count > 0;

    /// <summary>
    ///     Indicates whether the data loaded by LoadSongsAsync is already sorted.
    ///     Overriding this to true in a derived class will prevent an extra in-memory sort.
    /// </summary>
    protected virtual bool IsDataPreSortedAfterLoad => false;

    /// <summary>
    ///     When implemented in a derived class, loads the collection of songs to be displayed.
    /// </summary>
    protected abstract Task<IEnumerable<Song>> LoadSongsAsync();

    /// <summary>
    ///     Loads or re-sorts the song list based on the current sort order.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteLoadCommands))]
    public async Task RefreshOrSortSongsAsync(string? sortOrderString = null)
    {
        if (IsOverallLoading) return;

        if (!string.IsNullOrEmpty(sortOrderString) &&
            Enum.TryParse<SongSortOrder>(sortOrderString, true, out var newSortOrder)) CurrentSortOrder = newSortOrder;

        IsOverallLoading = true;
        Debug.WriteLine($"[SongListViewModelBase] Loading/sorting songs with order: {CurrentSortOrder}...");
        UpdateSortOrderButtonText(CurrentSortOrder);

        try
        {
            var fetchedSongs = await LoadSongsAsync() ?? Enumerable.Empty<Song>();

            //
            // Only perform an in-memory sort if the derived class indicates the data isn't already sorted.
            // This improves efficiency for sources that can sort at the query level (e.g., a database).
            //
            var songsToDisplay = IsDataPreSortedAfterLoad
                ? fetchedSongs
                : SortSongs(fetchedSongs, CurrentSortOrder);

            Songs = new ObservableCollection<Song>(songsToDisplay);
            TotalItemsText = $"{Songs.Count} {(Songs.Count == 1 ? "item" : "items")}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SongListViewModelBase] Error loading/sorting songs: {ex.Message}");
            TotalItemsText = "Error loading items";
        }
        finally
        {
            IsOverallLoading = false;
            Debug.WriteLine("[SongListViewModelBase] Song load/sort finished.");
        }
    }

    /// <summary>
    ///     Fetches all available playlists to populate UI elements like "Add to Playlist" menus.
    /// </summary>
    public async Task LoadAvailablePlaylistsAsync()
    {
        try
        {
            var playlists = await _libraryService.GetAllPlaylistsAsync();
            AvailablePlaylists = new ObservableCollection<Playlist>(playlists);
            AddSelectedSongsToPlaylistCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SongListViewModelBase] Error loading available playlists: {ex.Message}");
        }
    }

    /// <summary>
    ///     Updates the collection of selected songs based on UI interaction.
    /// </summary>
    public void OnSongsSelectionChanged(IEnumerable<object> selectedItems)
    {
        SelectedSongs.Clear();
        foreach (var item in selectedItems.OfType<Song>()) SelectedSongs.Add(item);
        IsSingleSongSelected = SelectedSongs.Count == 1;
        UpdateSelectionDependentCommands();
    }

    #region Playback and Queue Commands

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    public async Task PlayAllSongsAsync()
    {
        await EnsureRepeatOneIsOffAsync();
        await _playbackService.PlayAsync(Songs.ToList());
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    public async Task ShuffleAndPlayAllSongsAsync()
    {
        await EnsureRepeatOneIsOffAsync();
        await _playbackService.PlayAsync(Songs.ToList(), 0, true);
    }

    [RelayCommand]
    public async Task PlaySongAsync(Song? song)
    {
        if (song == null) return;
        await EnsureRepeatOneIsOffAsync();

        var startIndex = Songs.IndexOf(song);
        if (startIndex != -1)
            await _playbackService.PlayAsync(Songs.ToList(), startIndex);
        else
            //
            // Fallback for cases where the song might not be in the current list (e.g., search results).
            //
            await _playbackService.PlayAsync(song);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    public async Task PlaySelectedSongsAsync()
    {
        await EnsureRepeatOneIsOffAsync();
        await _playbackService.PlayAsync(SelectedSongs.ToList());
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    public async Task PlaySelectedSongsNextAsync()
    {
        foreach (var song in SelectedSongs.Reverse()) await _playbackService.PlayNextAsync(song);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    public async Task AddSelectedSongsToQueueAsync()
    {
        await _playbackService.AddRangeToQueueAsync(SelectedSongs);
    }

    [RelayCommand(CanExecute = nameof(CanAddSelectedSongsToPlaylist))]
    public async Task AddSelectedSongsToPlaylistAsync(Playlist? playlist)
    {
        if (playlist == null || !SelectedSongs.Any()) return;
        var songIdsToAdd = SelectedSongs.Select(s => s.Id).ToList();
        await _libraryService.AddSongsToPlaylistAsync(playlist.Id, songIdsToAdd);
    }

    #endregion

    #region Context Menu Commands

    [RelayCommand(CanExecute = nameof(CanExecuteSingleSongCommands))]
    public async Task ShowInFileExplorerAsync()
    {
        var song = SelectedSongs.FirstOrDefault();
        if (song == null || string.IsNullOrEmpty(song.FilePath) || !File.Exists(song.FilePath)) return;

        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(song.FilePath));
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SongListViewModelBase] Error showing file in explorer: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSingleSongCommands))]
    public void GoToAlbum()
    {
        var song = SelectedSongs.FirstOrDefault();
        if (song?.AlbumId == null || song.Album == null) return;

        // *** THIS IS THE CORRECTED CODE ***
        var navParam = new AlbumViewNavigationParameter
        {
            AlbumId = song.Album.Id,
            // FIX 1: The property is named AlbumTitle, not AlbumName.
            AlbumTitle = song.Album.Title,
            // FIX 2: Assign the artist's name (string), not the whole Album object.
            // Use null-safe access to prevent errors if the artist is missing.
            ArtistName = song.Album.Artist?.Name ?? "Unknown Artist"
        };
        // FIX 3: Uncommented the navigation call.
        _navigationService.Navigate(typeof(AlbumViewPage), navParam);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSingleSongCommands))]
    public void GoToArtist()
    {
        var song = SelectedSongs.FirstOrDefault();
        if (song?.ArtistId == null || song.Artist == null) return;
        var navParam = new ArtistViewNavigationParameter { ArtistId = song.Artist.Id, ArtistName = song.Artist.Name };
        _navigationService.Navigate(typeof(ArtistViewPage), navParam);
    }

    #endregion

    #region CanExecute and Helper Methods

    private bool CanExecuteLoadCommands()
    {
        return !IsOverallLoading;
    }

    private bool CanExecutePlayAllCommands()
    {
        return !IsOverallLoading && Songs.Any();
    }

    private bool CanExecuteSelectedSongsCommands()
    {
        return HasSelectedSongs;
    }

    private bool CanExecuteSingleSongCommands()
    {
        return IsSingleSongSelected;
    }

    private bool CanAddSelectedSongsToPlaylist()
    {
        return HasSelectedSongs && AvailablePlaylists.Any();
    }

    protected void UpdateSortOrderButtonText(SongSortOrder sortOrder)
    {
        CurrentSortOrderText = sortOrder switch
        {
            SongSortOrder.TitleAsc => "Sort By: A to Z",
            SongSortOrder.TitleDesc => "Sort By: Z to A",
            SongSortOrder.DateAddedDesc => "Sort By: Newest",
            SongSortOrder.DateAddedAsc => "Sort By: Oldest",
            SongSortOrder.AlbumAsc => "Sort By: Album",
            SongSortOrder.ArtistAsc => "Sort By: Artist",
            _ => "Sort By: A to Z"
        };
    }

    private void UpdateSelectionDependentCommands()
    {
        OnPropertyChanged(nameof(HasSelectedSongs));
        PlaySelectedSongsCommand.NotifyCanExecuteChanged();
        PlaySelectedSongsNextCommand.NotifyCanExecuteChanged();
        AddSelectedSongsToQueueCommand.NotifyCanExecuteChanged();
        AddSelectedSongsToPlaylistCommand.NotifyCanExecuteChanged();
    }

    private async Task EnsureRepeatOneIsOffAsync()
    {
        if (_playbackService.CurrentRepeatMode == RepeatMode.RepeatOne)
            await _playbackService.SetRepeatModeAsync(RepeatMode.Off);
    }

    protected static IEnumerable<Song> SortSongs(IEnumerable<Song> songs, SongSortOrder sortOrder)
    {
        return sortOrder switch
        {
            SongSortOrder.TitleDesc => songs.OrderByDescending(s => s.Title, StringComparer.OrdinalIgnoreCase),
            SongSortOrder.DateAddedDesc => songs.OrderByDescending(s => s.DateAddedToLibrary),
            SongSortOrder.DateAddedAsc => songs.OrderBy(s => s.DateAddedToLibrary),
            SongSortOrder.AlbumAsc => songs.OrderBy(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.TrackNumber),
            SongSortOrder.ArtistAsc => songs.OrderBy(s => s.Artist?.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Album?.Title).ThenBy(s => s.TrackNumber),
            _ => songs.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
        };
    }

    #endregion
}