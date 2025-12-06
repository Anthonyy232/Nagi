using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     A base class for view models that display a list of songs.
///     Provides common functionality for loading, sorting, selection, and playback.
/// </summary>
public abstract partial class SongListViewModelBase : ObservableObject
{
    private const int PageSize = 250;
    protected readonly IDispatcherService _dispatcherService;
    protected readonly ILibraryReader _libraryReader;
    private readonly object _loadLock = new();
    protected readonly ILogger _logger;
    protected readonly INavigationService _navigationService;
    protected readonly IMusicPlaybackService _playbackService;
    protected readonly IPlaylistService _playlistService;
    private readonly object _stateLock = new();
    private readonly IUIService _uiService;

    private int _currentPage;

    // For paged views, this holds all song IDs to enable "Play All" without loading all song objects into memory.
    private List<Guid> _fullSongIdList = new();
    private bool _hasNextPage;

    // Used to cancel any ongoing paged loading, e.g., when the user changes the sort order.
    private CancellationTokenSource? _pagedLoadCts;
    private int _totalItemCount;

    protected SongListViewModelBase(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger logger)
    {
        _libraryReader = libraryReader ?? throw new ArgumentNullException(nameof(libraryReader));
        _playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    [ObservableProperty] public partial ObservableCollection<Song> Songs { get; set; } = new();

    partial void OnSongsChanged(ObservableCollection<Song> value)
    {
        OnSongsCollectionChanged();
    }

    protected virtual void OnSongsCollectionChanged()
    {
        // Override in derived classes to react to Songs collection changes
    }

    [ObservableProperty] public partial ObservableCollection<Song> SelectedSongs { get; set; } = new();

    [ObservableProperty] public partial ObservableCollection<Playlist> AvailablePlaylists { get; set; } = new();

    [ObservableProperty] public partial string PageTitle { get; set; } = "Songs";

    [ObservableProperty] public partial string TotalItemsText { get; set; } = "0 items";

    [ObservableProperty] public partial SongSortOrder CurrentSortOrder { get; set; } = SongSortOrder.TitleAsc;

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = "Sort By: A to Z";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshOrSortSongsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayAllSongsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShuffleAndPlayAllSongsCommand))]
    public partial bool IsOverallLoading { get; set; }

    [ObservableProperty] public partial bool IsLoadingNextPage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowInFileExplorerCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToAlbumCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToArtistCommand))]
    public partial bool IsSingleSongSelected { get; set; }

    public bool HasSelectedSongs => SelectedSongs.Any();
    protected virtual bool IsDataPreSortedAfterLoad => false;
    protected virtual bool IsPagingSupported => false;
    protected abstract Task<IEnumerable<Song>> LoadSongsAsync();

    protected virtual Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder)
    {
        return Task.FromResult(new PagedResult<Song>());
    }

    protected virtual Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        return Task.FromResult(new List<Guid>());
    }

    [RelayCommand(CanExecute = nameof(CanExecuteLoadCommands))]
    public async Task RefreshOrSortSongsAsync(string? sortOrderString = null)
    {
        // Prevent concurrent load/sort operations from interfering with each other.
        lock (_loadLock)
        {
            if (IsOverallLoading) return;
            IsOverallLoading = true;
        }

        _logger.LogInformation("Starting song refresh. Paging: {IsPagingSupported}", IsPagingSupported);
        // Cancel any previous loading task, as it's now obsolete.
        _pagedLoadCts?.Cancel();

        if (!string.IsNullOrEmpty(sortOrderString) &&
            Enum.TryParse<SongSortOrder>(sortOrderString, true, out var newSortOrder))
        {
            CurrentSortOrder = newSortOrder;
            _logger.LogInformation("Sort order changed to '{SortOrder}'", CurrentSortOrder);
        }

        UpdateSortOrderButtonText(CurrentSortOrder);

        try
        {
            if (IsPagingSupported)
            {
                // First, get all song IDs for the current view/sort order.
                _fullSongIdList = await LoadAllSongIdsAsync(CurrentSortOrder);
                _pagedLoadCts = new CancellationTokenSource();
                var token = _pagedLoadCts.Token;

                // Load the first page to display something to the user quickly.
                var pagedResult = await LoadSongsPagedAsync(1, PageSize, CurrentSortOrder);
                ProcessPagedResult(pagedResult, token);

                bool hasMore;
                lock (_stateLock)
                {
                    hasMore = _hasNextPage;
                }

                // If there are more pages, start loading them automatically in the background.
                if (hasMore && !token.IsCancellationRequested) _ = StartAutomaticPagedLoadingAsync(token);
            }
            else
            {
                // For non-paged views, load all songs at once.
                var fetchedSongs = await LoadSongsAsync() ?? Enumerable.Empty<Song>();
                var songsToDisplay = IsDataPreSortedAfterLoad
                    ? fetchedSongs
                    : SortSongs(fetchedSongs, CurrentSortOrder);
                Songs = new ObservableCollection<Song>(songsToDisplay);
                TotalItemsText = $"{Songs.Count} {(Songs.Count == 1 ? "item" : "items")}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load or sort songs");
            TotalItemsText = "Error loading items";
        }
        finally
        {
            IsOverallLoading = false;
        }
    }

    /// <summary>
    ///     Transparently loads subsequent pages in the background to create a smooth "infinite scroll" experience.
    /// </summary>
    private async Task StartAutomaticPagedLoadingAsync(CancellationToken token)
    {
        try
        {
            bool hasMore;
            lock (_stateLock)
            {
                hasMore = _hasNextPage;
            }

            while (hasMore && !token.IsCancellationRequested)
            {
                // A small delay to prevent overwhelming the system and to allow UI to remain responsive.
                await Task.Delay(250, token);
                if (token.IsCancellationRequested) break;

                int nextPage;
                lock (_stateLock)
                {
                    nextPage = _currentPage + 1;
                }

                var pagedResult = await LoadSongsPagedAsync(nextPage, PageSize, CurrentSortOrder);
                ProcessPagedResult(pagedResult, token, true);

                lock (_stateLock)
                {
                    hasMore = _hasNextPage;
                }
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Automatic page loading was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during automatic page loading");
        }
    }

    public async Task LoadAvailablePlaylistsAsync()
    {
        try
        {
            var playlists = await _libraryReader.GetAllPlaylistsAsync();
            AvailablePlaylists = new ObservableCollection<Playlist>(playlists);
            AddSelectedSongsToPlaylistCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load available playlists");
        }
    }

    public void OnSongsSelectionChanged(IEnumerable<object> selectedItems)
    {
        SelectedSongs.Clear();
        foreach (var item in selectedItems.OfType<Song>()) SelectedSongs.Add(item);
        IsSingleSongSelected = SelectedSongs.Count == 1;
        UpdateSelectionDependentCommands();
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private async Task PlayAllSongsAsync()
    {
        await EnsureRepeatOneIsOffAsync();
        if (IsPagingSupported)
        {
            // For paged views, we use the full ID list to build the queue, which is more memory efficient.
            var songMap = await _libraryReader.GetSongsByIdsAsync(_fullSongIdList);
            if (!songMap.Any()) return;
            var orderedSongs = _fullSongIdList
                .Select(id => songMap.TryGetValue(id, out var song) ? song : null)
                .Where(song => song != null)
                .ToList();
            await _playbackService.PlayAsync(orderedSongs!);
        }
        else
        {
            await _playbackService.PlayAsync(Songs.ToList());
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private async Task ShuffleAndPlayAllSongsAsync()
    {
        await EnsureRepeatOneIsOffAsync();
        if (IsPagingSupported)
        {
            // For paged views, we fetch all songs by their IDs to shuffle and play them.
            var songsToPlay = await _libraryReader.GetSongsByIdsAsync(_fullSongIdList);
            if (songsToPlay.Any()) await _playbackService.PlayAsync(songsToPlay.Values.ToList(), 0, true);
        }
        else
        {
            await _playbackService.PlayAsync(Songs.ToList(), 0, true);
        }
    }

    [RelayCommand]
    private async Task PlaySongAsync(Song? song)
    {
        if (song == null) return;
        await EnsureRepeatOneIsOffAsync();

        if (IsPagingSupported)
        {
            // For paged views, find the song's index in the full ID list to build the correct queue.
            var startIndex = _fullSongIdList.IndexOf(song.Id);
            if (startIndex == -1)
            {
                // Fallback for a song not in the list for some reason.
                await _playbackService.PlayAsync(song);
                return;
            }

            var songMap = await _libraryReader.GetSongsByIdsAsync(_fullSongIdList);
            if (!songMap.Any()) return;
            var orderedSongs = _fullSongIdList
                .Select(id => songMap.TryGetValue(id, out var s) ? s : null)
                .Where(s => s != null)
                .ToList();
            await _playbackService.PlayAsync(orderedSongs!, startIndex);
        }
        else
        {
            var startIndex = Songs.IndexOf(song);
            if (startIndex != -1)
                await _playbackService.PlayAsync(Songs.ToList(), startIndex);
            else
                await _playbackService.PlayAsync(song);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private async Task PlaySelectedSongsAsync()
    {
        await EnsureRepeatOneIsOffAsync();
        await _playbackService.PlayAsync(SelectedSongs.ToList());
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private async Task PlaySelectedSongsNextAsync()
    {
        // Reverse the list so songs are added in the selected order after the current track.
        foreach (var song in SelectedSongs.Reverse()) await _playbackService.PlayNextAsync(song);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private async Task AddSelectedSongsToQueueAsync()
    {
        await _playbackService.AddRangeToQueueAsync(SelectedSongs);
    }

    [RelayCommand(CanExecute = nameof(CanAddSelectedSongsToPlaylist))]
    private async Task AddSelectedSongsToPlaylistAsync(Playlist? playlist)
    {
        if (playlist == null || !SelectedSongs.Any()) return;
        var songIdsToAdd = SelectedSongs.Select(s => s.Id).ToList();
        await _playlistService.AddSongsToPlaylistAsync(playlist.Id, songIdsToAdd);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSingleSongCommands))]
    private async Task ShowInFileExplorerAsync(Song? song)
    {
        var targetSong = song ?? SelectedSongs.FirstOrDefault();
        if (targetSong?.FilePath is null) return;
        try
        {
            await _uiService.OpenFolderInExplorerAsync(targetSong.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show file in explorer for path {FilePath}", targetSong.FilePath);
        }
    }

    [RelayCommand]
    private void GoToAlbum(Song? song)
    {
        var targetSong = song ?? SelectedSongs.FirstOrDefault();
        if (targetSong?.AlbumId == null || targetSong.Album == null) return;
        _logger.LogInformation("Navigating to album '{AlbumTitle}' ({AlbumId})", targetSong.Album.Title,
            targetSong.Album.Id);
        var navParam = new AlbumViewNavigationParameter
        {
            AlbumId = targetSong.Album.Id,
            AlbumTitle = targetSong.Album.Title,
            ArtistName = targetSong.Album.Artist?.Name ?? "Unknown Artist"
        };
        _navigationService.Navigate(typeof(AlbumViewPage), navParam);
    }

    [RelayCommand]
    private void GoToArtist(Song? song)
    {
        var targetSong = song ?? SelectedSongs.FirstOrDefault();
        if (targetSong?.ArtistId == null || targetSong.Artist == null) return;
        _logger.LogInformation("Navigating to artist '{ArtistName}' ({ArtistId})", targetSong.Artist.Name,
            targetSong.Artist.Id);
        var navParam = new ArtistViewNavigationParameter
        {
            ArtistId = targetSong.Artist.Id,
            ArtistName = targetSong.Artist.Name
        };
        _navigationService.Navigate(typeof(ArtistViewPage), navParam);
    }

    protected bool CanExecuteLoadCommands()
    {
        return !IsOverallLoading;
    }

    private bool CanExecutePlayAllCommands()
    {
        if (IsPagingSupported) return !IsOverallLoading && _fullSongIdList.Any();
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

    /// <summary>
    ///     Processes a page of results, updating the UI-bound collection and internal paging state.
    /// </summary>
    private void ProcessPagedResult(PagedResult<Song> pagedResult, CancellationToken token, bool append = false)
    {
        if (pagedResult?.Items == null || token.IsCancellationRequested) return;

        // All UI updates must be marshalled to the UI thread.
        _dispatcherService.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested) return;

            if (append)
                foreach (var song in pagedResult.Items)
                    Songs.Add(song);
            else
                Songs = new ObservableCollection<Song>(pagedResult.Items);
        });

        // Update internal state within a lock for thread safety.
        lock (_stateLock)
        {
            _hasNextPage = pagedResult.HasNextPage;
            _totalItemCount = pagedResult.TotalCount;
            _currentPage = pagedResult.PageNumber;
        }

        _dispatcherService.TryEnqueue(() =>
        {
            TotalItemsText = $"{pagedResult.TotalCount} {(pagedResult.TotalCount == 1 ? "item" : "items")}";
        });
    }

    protected void UpdateSortOrderButtonText(SongSortOrder sortOrder)
    {
        CurrentSortOrderText = sortOrder switch
        {
            SongSortOrder.TitleAsc => "Sort By: A to Z",
            SongSortOrder.TitleDesc => "Sort By: Z to A",
            SongSortOrder.DateAddedDesc => "Sort By: Newest",
            SongSortOrder.DateAddedAsc => "Sort By: Oldest",
            SongSortOrder.DateModifiedDesc => "Sort By: Modified (Newest)",
            SongSortOrder.DateModifiedAsc => "Sort By: Modified (Oldest)",
            SongSortOrder.AlbumAsc => "Sort By: Album",
            SongSortOrder.ArtistAsc => "Sort By: Artist",
            SongSortOrder.TrackNumberAsc => "Sort By: Disc",
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

    /// <summary>
    ///     A UX improvement to ensure that when a user explicitly plays a list,
    ///     it doesn't get stuck on the first song if "Repeat One" was previously enabled.
    /// </summary>
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
            SongSortOrder.DateModifiedDesc => songs.OrderByDescending(s => s.FileModifiedDate),
            SongSortOrder.DateModifiedAsc => songs.OrderBy(s => s.FileModifiedDate),
            SongSortOrder.AlbumAsc => songs.OrderBy(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber),
            SongSortOrder.ArtistAsc => songs.OrderBy(s => s.Artist?.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Album?.Title)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber),
            _ => songs.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    ///     Cleans up resources, particularly stopping any in-flight background loading tasks.
    /// </summary>
    public virtual void Cleanup()
    {
        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();
        _pagedLoadCts = null;
        _logger.LogDebug("Cleaned up resources");
    }
}