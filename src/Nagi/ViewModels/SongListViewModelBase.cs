using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Nagi.Models;
using Nagi.Navigation;
using Nagi.Pages;
using Nagi.Services.Abstractions;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace Nagi.ViewModels;

/// <summary>
/// A base view model for pages that display a list of songs, providing common functionality
/// for loading, sorting, playback, and selection.
/// This base class supports both one-shot loading and automatic, continuous paged loading.
/// </summary>
public abstract partial class SongListViewModelBase : ObservableObject {
    protected readonly ILibraryService _libraryService;
    protected readonly INavigationService _navigationService;
    protected readonly IMusicPlaybackService _playbackService;

    private const int PageSize = 250;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _stateLock = new();

    private CancellationTokenSource? _pagedLoadCts;
    private List<Guid> _fullSongIdList = new();
    private int _currentPage;
    private bool _hasNextPage;
    private int _totalItemCount;

    [ObservableProperty]
    private ObservableCollection<Song> _songs = new();

    [ObservableProperty]
    private ObservableCollection<Song> _selectedSongs = new();

    [ObservableProperty]
    private ObservableCollection<Playlist> _availablePlaylists = new();

    [ObservableProperty]
    private string _pageTitle = "Songs";

    [ObservableProperty]
    private string _totalItemsText = "0 items";

    [ObservableProperty]
    private SongSortOrder _currentSortOrder = SongSortOrder.TitleAsc;

    [ObservableProperty]
    private string _currentSortOrderText = "Sort By: A to Z";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshOrSortSongsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayAllSongsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShuffleAndPlayAllSongsCommand))]
    private bool _isOverallLoading;

    [ObservableProperty]
    private bool _isLoadingNextPage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowInFileExplorerCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToAlbumCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToArtistCommand))]
    private bool _isSingleSongSelected;

    protected SongListViewModelBase(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService) {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    /// <summary>
    /// Gets a value indicating whether any songs are currently selected.
    /// </summary>
    public bool HasSelectedSongs => SelectedSongs.Any();

    /// <summary>
    /// Gets a value indicating whether the data source provides pre-sorted data.
    /// If false, the songs will be sorted client-side after being fetched.
    /// </summary>
    protected virtual bool IsDataPreSortedAfterLoad => false;

    /// <summary>
    /// Gets a value indicating whether the view model should load songs in pages.
    /// </summary>
    protected virtual bool IsPagingSupported => false;

    /// <summary>
    /// When implemented in a derived class, loads the complete list of songs.
    /// This method is used when <see cref="IsPagingSupported"/> is false.
    /// </summary>
    protected abstract Task<IEnumerable<Song>> LoadSongsAsync();

    /// <summary>
    /// When implemented in a derived class, loads a specific page of songs.
    /// This method is used when <see cref="IsPagingSupported"/> is true.
    /// </summary>
    protected virtual Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) {
        return Task.FromResult(new PagedResult<Song>());
    }

    /// <summary>
    /// When implemented in a derived class, loads the complete list of song IDs for the current view.
    /// This is used for playback purposes when paging is enabled.
    /// </summary>
    protected virtual Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        return Task.FromResult(new List<Guid>());
    }

    /// <summary>
    /// Loads or reloads the song list. If a sort order is provided, it sorts the list accordingly.
    /// This method orchestrates both paged and non-paged loading strategies.
    /// </summary>
    /// <param name="sortOrderString">An optional string representing the <see cref="SongSortOrder"/> to apply.</param>
    [RelayCommand(CanExecute = nameof(CanExecuteLoadCommands))]
    public async Task RefreshOrSortSongsAsync(string? sortOrderString = null) {
        if (IsOverallLoading) return;

        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();

        if (!string.IsNullOrEmpty(sortOrderString) &&
            Enum.TryParse<SongSortOrder>(sortOrderString, true, out var newSortOrder)) {
            CurrentSortOrder = newSortOrder;
        }

        IsOverallLoading = true;
        UpdateSortOrderButtonText(CurrentSortOrder);

        try {
            if (IsPagingSupported) {
                _fullSongIdList = await LoadAllSongIdsAsync(CurrentSortOrder);
                _pagedLoadCts = new CancellationTokenSource();
                var token = _pagedLoadCts.Token;

                var pagedResult = await LoadSongsPagedAsync(1, PageSize, CurrentSortOrder);
                ProcessPagedResult(pagedResult, token);

                bool hasMore;
                lock (_stateLock) { hasMore = _hasNextPage; }

                if (hasMore && !token.IsCancellationRequested) {
                    _ = StartAutomaticPagedLoadingAsync(token);
                }
            }
            else {
                var fetchedSongs = await LoadSongsAsync() ?? Enumerable.Empty<Song>();
                var songsToDisplay = IsDataPreSortedAfterLoad
                    ? fetchedSongs
                    : SortSongs(fetchedSongs, CurrentSortOrder);
                Songs = new ObservableCollection<Song>(songsToDisplay);
                TotalItemsText = $"{Songs.Count} {(Songs.Count == 1 ? "item" : "items")}";
            }
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] SongListViewModelBase: Failed to load or sort songs. {ex.Message}");
            TotalItemsText = "Error loading items";
        }
        finally {
            IsOverallLoading = false;
        }
    }

    /// <summary>
    /// Initiates a background task that automatically loads subsequent pages of songs
    /// until all pages are loaded or the operation is cancelled.
    /// </summary>
    private async Task StartAutomaticPagedLoadingAsync(CancellationToken token) {
        try {
            bool hasMore;
            lock (_stateLock) { hasMore = _hasNextPage; }

            while (hasMore && !token.IsCancellationRequested) {
                await Task.Delay(250, token);
                if (token.IsCancellationRequested) break;

                int nextPage;
                lock (_stateLock) { nextPage = _currentPage + 1; }

                var pagedResult = await LoadSongsPagedAsync(nextPage, PageSize, CurrentSortOrder);
                ProcessPagedResult(pagedResult, token, append: true);

                lock (_stateLock) { hasMore = _hasNextPage; }
            }
        }
        catch (TaskCanceledException) {
            // This is expected when the operation is cancelled.
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] SongListViewModelBase: Failed during automatic page loading. {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches the list of all available playlists from the library.
    /// </summary>
    public async Task LoadAvailablePlaylistsAsync() {
        try {
            var playlists = await _libraryService.GetAllPlaylistsAsync();
            AvailablePlaylists = new ObservableCollection<Playlist>(playlists);
            AddSelectedSongsToPlaylistCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] SongListViewModelBase: Failed to load available playlists. {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the collection of selected songs based on the UI selection.
    /// </summary>
    /// <param name="selectedItems">The new collection of selected items.</param>
    public void OnSongsSelectionChanged(IEnumerable<object> selectedItems) {
        SelectedSongs.Clear();
        foreach (var item in selectedItems.OfType<Song>()) {
            SelectedSongs.Add(item);
        }
        IsSingleSongSelected = SelectedSongs.Count == 1;
        UpdateSelectionDependentCommands();
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private async Task PlayAllSongsAsync() {
        await EnsureRepeatOneIsOffAsync();
        if (IsPagingSupported) {
            var songMap = await _libraryService.GetSongsByIdsAsync(_fullSongIdList);
            if (!songMap.Any()) return;
            var orderedSongs = _fullSongIdList
                .Select(id => songMap.TryGetValue(id, out var song) ? song : null)
                .Where(song => song != null)
                .ToList();
            await _playbackService.PlayAsync(orderedSongs!);
        }
        else {
            await _playbackService.PlayAsync(Songs.ToList());
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private async Task ShuffleAndPlayAllSongsAsync() {
        await EnsureRepeatOneIsOffAsync();
        if (IsPagingSupported) {
            var songsToPlay = await _libraryService.GetSongsByIdsAsync(_fullSongIdList);
            if (songsToPlay.Any()) {
                await _playbackService.PlayAsync(songsToPlay.Values.ToList(), 0, true);
            }
        }
        else {
            await _playbackService.PlayAsync(Songs.ToList(), 0, true);
        }
    }

    [RelayCommand]
    private async Task PlaySongAsync(Song? song) {
        if (song == null) return;
        await EnsureRepeatOneIsOffAsync();

        if (IsPagingSupported) {
            var startIndex = _fullSongIdList.IndexOf(song.Id);
            if (startIndex == -1) {
                await _playbackService.PlayAsync(song);
                return;
            }
            var songMap = await _libraryService.GetSongsByIdsAsync(_fullSongIdList);
            if (!songMap.Any()) return;
            var orderedSongs = _fullSongIdList
                .Select(id => songMap.TryGetValue(id, out var s) ? s : null)
                .Where(s => s != null)
                .ToList();
            await _playbackService.PlayAsync(orderedSongs!, startIndex);
        }
        else {
            var startIndex = Songs.IndexOf(song);
            if (startIndex != -1) {
                await _playbackService.PlayAsync(Songs.ToList(), startIndex);
            }
            else {
                await _playbackService.PlayAsync(song);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private async Task PlaySelectedSongsAsync() {
        await EnsureRepeatOneIsOffAsync();
        await _playbackService.PlayAsync(SelectedSongs.ToList());
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private async Task PlaySelectedSongsNextAsync() {
        foreach (var song in SelectedSongs.Reverse()) {
            await _playbackService.PlayNextAsync(song);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private async Task AddSelectedSongsToQueueAsync() {
        await _playbackService.AddRangeToQueueAsync(SelectedSongs);
    }

    [RelayCommand(CanExecute = nameof(CanAddSelectedSongsToPlaylist))]
    private async Task AddSelectedSongsToPlaylistAsync(Playlist? playlist) {
        if (playlist == null || !SelectedSongs.Any()) return;
        var songIdsToAdd = SelectedSongs.Select(s => s.Id).ToList();
        await _libraryService.AddSongsToPlaylistAsync(playlist.Id, songIdsToAdd);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSingleSongCommands))]
    private async Task ShowInFileExplorerAsync(Song? song) {
        var targetSong = song ?? SelectedSongs.FirstOrDefault();
        if (targetSong == null || string.IsNullOrEmpty(targetSong.FilePath) || !File.Exists(targetSong.FilePath)) return;
        try {
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(targetSong.FilePath));
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] SongListViewModelBase: Failed to show file in explorer. {ex.Message}");
        }
    }

    [RelayCommand]
    private void GoToAlbum(Song? song) {
        var targetSong = song ?? SelectedSongs.FirstOrDefault();
        if (targetSong?.AlbumId == null || targetSong.Album == null) return;
        var navParam = new AlbumViewNavigationParameter {
            AlbumId = targetSong.Album.Id,
            AlbumTitle = targetSong.Album.Title,
            ArtistName = targetSong.Album.Artist?.Name ?? "Unknown Artist"
        };
        _navigationService.Navigate(typeof(AlbumViewPage), navParam);
    }

    [RelayCommand]
    private void GoToArtist(Song? song) {
        var targetSong = song ?? SelectedSongs.FirstOrDefault();
        if (targetSong?.ArtistId == null || targetSong.Artist == null) return;
        var navParam = new ArtistViewNavigationParameter {
            ArtistId = targetSong.Artist.Id,
            ArtistName = targetSong.Artist.Name
        };
        _navigationService.Navigate(typeof(ArtistViewPage), navParam);
    }

    protected bool CanExecuteLoadCommands() => !IsOverallLoading;

    private bool CanExecutePlayAllCommands() {
        if (IsPagingSupported) {
            return !IsOverallLoading && _fullSongIdList.Any();
        }
        return !IsOverallLoading && Songs.Any();
    }

    private bool CanExecuteSelectedSongsCommands() => HasSelectedSongs;
    private bool CanExecuteSingleSongCommands() => IsSingleSongSelected;
    private bool CanAddSelectedSongsToPlaylist() => HasSelectedSongs && AvailablePlaylists.Any();

    /// <summary>
    /// Processes a page of song results, updating the main songs collection and pagination state.
    /// </summary>
    /// <param name="pagedResult">The paged result to process.</param>
    /// <param name="token">The cancellation token to check before modifying collections.</param>
    /// <param name="append">If true, appends items to the existing collection; otherwise, replaces it.</param>
    private void ProcessPagedResult(PagedResult<Song> pagedResult, CancellationToken token, bool append = false) {
        if (pagedResult?.Items == null || token.IsCancellationRequested) return;

        _dispatcherQueue.TryEnqueue(() => {
            if (token.IsCancellationRequested) return;

            if (append) {
                foreach (var song in pagedResult.Items) Songs.Add(song);
            }
            else {
                Songs = new ObservableCollection<Song>(pagedResult.Items);
            }
        });

        lock (_stateLock) {
            _hasNextPage = pagedResult.HasNextPage;
            _totalItemCount = pagedResult.TotalCount;
            _currentPage = pagedResult.PageNumber;
        }

        _dispatcherQueue.TryEnqueue(() => {
            TotalItemsText = $"{pagedResult.TotalCount} {(pagedResult.TotalCount == 1 ? "item" : "items")}";
        });
    }

    protected void UpdateSortOrderButtonText(SongSortOrder sortOrder) {
        CurrentSortOrderText = sortOrder switch {
            SongSortOrder.TitleAsc => "Sort By: A to Z",
            SongSortOrder.TitleDesc => "Sort By: Z to A",
            SongSortOrder.DateAddedDesc => "Sort By: Newest",
            SongSortOrder.DateAddedAsc => "Sort By: Oldest",
            SongSortOrder.AlbumAsc => "Sort By: Album",
            SongSortOrder.ArtistAsc => "Sort By: Artist",
            _ => "Sort By: A to Z"
        };
    }

    private void UpdateSelectionDependentCommands() {
        OnPropertyChanged(nameof(HasSelectedSongs));
        PlaySelectedSongsCommand.NotifyCanExecuteChanged();
        PlaySelectedSongsNextCommand.NotifyCanExecuteChanged();
        AddSelectedSongsToQueueCommand.NotifyCanExecuteChanged();
        AddSelectedSongsToPlaylistCommand.NotifyCanExecuteChanged();
    }

    private async Task EnsureRepeatOneIsOffAsync() {
        if (_playbackService.CurrentRepeatMode == RepeatMode.RepeatOne) {
            await _playbackService.SetRepeatModeAsync(RepeatMode.Off);
        }
    }

    protected static IEnumerable<Song> SortSongs(IEnumerable<Song> songs, SongSortOrder sortOrder) {
        return sortOrder switch {
            SongSortOrder.TitleDesc => songs.OrderByDescending(s => s.Title, StringComparer.OrdinalIgnoreCase),
            SongSortOrder.DateAddedDesc => songs.OrderByDescending(s => s.DateAddedToLibrary),
            SongSortOrder.DateAddedAsc => songs.OrderBy(s => s.DateAddedToLibrary),
            SongSortOrder.AlbumAsc => songs.OrderBy(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.TrackNumber),
            SongSortOrder.ArtistAsc => songs.OrderBy(s => s.Artist?.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Album?.Title).ThenBy(s => s.TrackNumber),
            _ => songs.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Cleans up resources used by the view model, such as cancelling any ongoing load operations.
    /// </summary>
    public virtual void Cleanup() {
        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();
        _pagedLoadCts = null;
    }
}