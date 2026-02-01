using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     A base class for view models that display a list of songs.
///     Provides common functionality for loading, sorting, selection, and playback.
/// </summary>
public abstract partial class SongListViewModelBase : SearchableViewModelBase, IDisposable
{
    protected const int PageSize = 250;
    protected readonly ILibraryReader _libraryReader;
    private readonly object _loadLock = new();
    protected readonly INavigationService _navigationService;
    protected readonly IMusicPlaybackService _playbackService;
    protected readonly IPlaylistService _playlistService;
    protected readonly ReaderWriterLockSlim _stateLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly IUIService _uiService;
    protected readonly IMusicNavigationService _musicNavigationService;
    private bool _isDisposed;

    protected int _currentPage;

    // For paged views, this holds all song IDs to enable "Play All" without loading all song objects into memory.
    protected List<Guid> _fullSongIdList = new();
    protected bool _hasNextPage;

    // Used to cancel any ongoing paged loading, e.g., when the user changes the sort order.
    private CancellationTokenSource? _pagedLoadCts;
    protected int _totalItemCount;
    
    // Navigation re-entry guards
    private bool _isNavigatingToArtist;
    private bool _isNavigatingToAlbum;

    /// <summary>
    ///     Gets the logical selection state for this view.
    /// </summary>
    public SelectionState SelectionState { get; } = new();

    protected SongListViewModelBase(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger logger)
        : base(dispatcherService, logger)
    {
        _libraryReader = libraryReader ?? throw new ArgumentNullException(nameof(libraryReader));
        _playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _musicNavigationService = musicNavigationService ?? throw new ArgumentNullException(nameof(musicNavigationService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));

        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirstSong))]
    public partial ObservableCollection<Song> Songs { get; set; } = new();

    public Song? FirstSong => Songs?.FirstOrDefault();

    partial void OnSongsChanged(ObservableCollection<Song> oldValue, ObservableCollection<Song> newValue)
    {
        OnSongsChangedInternal(oldValue, newValue);
    }

    protected virtual void OnSongsChangedInternal(ObservableCollection<Song> oldValue, ObservableCollection<Song> newValue)
    {
    }



    [ObservableProperty] public partial ObservableCollection<Song> SelectedSongs { get; set; } = new();

    [ObservableProperty] public partial ObservableCollection<Playlist> AvailablePlaylists { get; set; } = new();

    [ObservableProperty] public partial string PageTitle { get; set; } = Nagi.WinUI.Resources.Strings.SongList_PageTitle_Default;

    [ObservableProperty] public partial string TotalItemsText { get; set; } = Nagi.WinUI.Resources.Strings.SongList_TotalItems_Default;


    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlaySelectedSongsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlaySelectedSongsNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedSongsToQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedSongsToPlaylistCommand))]
    public partial string SelectedItemsCountText { get; set; } = string.Empty;

    public virtual int SelectedItemsCount
    {
        get
        {
            _stateLock.EnterReadLock();
            try
            {
                return SelectionState.GetSelectedCount(_fullSongIdList.Count);
            }
            finally
            {
                _stateLock.ExitReadLock();
            }
        }
    }
    
    [ObservableProperty] public partial SongSortOrder CurrentSortOrder { get; set; } = SongSortOrder.TitleAsc;

    partial void OnCurrentSortOrderChanged(SongSortOrder oldValue, SongSortOrder newValue)
    {
        OnCurrentSortOrderChangedInternal(oldValue, newValue);
    }

    protected virtual void OnCurrentSortOrderChangedInternal(SongSortOrder oldOrder, SongSortOrder newOrder)
    {
    }

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshOrSortSongsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayAllSongsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShuffleAndPlayAllSongsCommand))]
    public partial bool IsOverallLoading { get; set; }

    [ObservableProperty] public partial bool IsLoadingNextPage { get; set; }

    /// <summary>
    ///     Indicates whether background page loading is in progress.
    ///     Derived classes can use this to suppress side effects (e.g., reorder saves) during background loading.
    /// </summary>
    protected bool IsBackgroundPageLoading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowInFileExplorerCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToAlbumCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToArtistCommand))]
    public partial bool IsSingleSongSelected { get; set; }

    public bool HasSelectedSongs => SelectedItemsCount > 0;

    protected override void OnSearchTermChangedInternal(string value)
    {
        if (HasSelectedSongs) DeselectAll();
    }

    protected override async Task ExecuteSearchAsync(CancellationToken token)
    {
        await _dispatcherService.EnqueueAsync(async () =>
        {
            if (token.IsCancellationRequested) return;
            await RefreshOrSortSongsAsync(null, token);
        });
    }


    protected virtual Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder)
    {
        return Task.FromResult(new PagedResult<Song>());
    }

    protected virtual Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        return Task.FromResult(new List<Guid>());
    }

    [RelayCommand(CanExecute = nameof(CanExecuteLoadCommands))]
    public virtual async Task RefreshOrSortSongsAsync(string? sortOrderString = null, CancellationToken manualToken = default)
    {
        // Prevent concurrent load/sort operations from interfering with each other.
        lock (_loadLock)
        {
            if (IsOverallLoading) return;
            IsOverallLoading = true;
        }

        _logger.LogDebug("Starting song refresh.");
        // Cancel any previous loading task, as it's now obsolete.
        _pagedLoadCts?.Cancel();

        if (!string.IsNullOrEmpty(sortOrderString) &&
            Enum.TryParse<SongSortOrder>(sortOrderString, true, out var newSortOrder)
            && newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            _logger.LogDebug("Sort order changed to '{SortOrder}'", CurrentSortOrder);
            _ = SaveSortOrderAsync(newSortOrder)
                .ContinueWith(t => _logger.LogError(t.Exception, "Failed to save sort order"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        UpdateSortOrderButtonText(CurrentSortOrder);
        UpdateSelectionStatus();

        try
        {
            _pagedLoadCts = manualToken != default 
                ? CancellationTokenSource.CreateLinkedTokenSource(manualToken) 
                : new CancellationTokenSource();
            var token = _pagedLoadCts.Token;

            // Overlap fetching the full ID list (for Play All) with loading the first page (for display).
            var idsTask = LoadAllSongIdsAsync(CurrentSortOrder);
            var firstPageTask = LoadSongsPagedAsync(1, PageSize, CurrentSortOrder);

            await Task.WhenAll(idsTask, firstPageTask).ConfigureAwait(false);

            var pagedResult = firstPageTask.Result;

            _stateLock.EnterWriteLock();
            try
            {
                _fullSongIdList = idsTask.Result;
            }
            finally
            {
                _stateLock.ExitWriteLock();
            }

            ProcessPagedResult(pagedResult, token);

            bool hasMore;
            _stateLock.EnterReadLock();
            try
            {
                hasMore = _hasNextPage;
            }
            finally
            {
                _stateLock.ExitReadLock();
            }

            // If there are more pages, start loading them automatically in the background.
            if (hasMore && !token.IsCancellationRequested) _ = StartAutomaticPagedLoadingAsync(token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load or sort songs");
            TotalItemsText = Nagi.WinUI.Resources.Strings.SongList_ErrorLoading;
        }
        finally
        {
            IsOverallLoading = false;
            PlayAllSongsCommand.NotifyCanExecuteChanged();
            ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    ///     Transparently loads subsequent pages in the background to create a smooth "infinite scroll" experience.
    /// </summary>
    private async Task StartAutomaticPagedLoadingAsync(CancellationToken token)
    {
        IsBackgroundPageLoading = true;
        try
        {
            bool hasMore;
            _stateLock.EnterReadLock();
            try
            {
                hasMore = _hasNextPage;
            }
            finally
            {
                _stateLock.ExitReadLock();
            }

            while (hasMore && !token.IsCancellationRequested)
            {
                // A small delay to prevent overwhelming the system and to allow UI to remain responsive.
                await Task.Delay(250, token);
                if (token.IsCancellationRequested) break;

                int nextPage;
                _stateLock.EnterReadLock();
                try
                {
                    nextPage = _currentPage + 1;
                }
                finally
                {
                    _stateLock.ExitReadLock();
                }

                var pagedResult = await LoadSongsPagedAsync(nextPage, PageSize, CurrentSortOrder);
                ProcessPagedResult(pagedResult, token, true);

                _stateLock.EnterReadLock();
                try
                {
                    hasMore = _hasNextPage;
                }
                finally
                {
                    _stateLock.ExitReadLock();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Automatic page loading was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during automatic page loading");
        }
        finally
        {
            IsBackgroundPageLoading = false;
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
        UpdateSelectionStatus();
    }

    /// <summary>
    ///     Forces a "Select All" state for the current view.
    /// </summary>
    [RelayCommand]
    public virtual void SelectAll()
    {
        SelectionState.SelectAll();
        UpdateSelectionStatus();
        OnSelectionStateChanged();
    }

    /// <summary>
    ///     Clears the current selection state.
    /// </summary>
    [RelayCommand]
    public virtual void DeselectAll()
    {
        SelectionState.DeselectAll();
        UpdateSelectionStatus();
        OnSelectionStateChanged();
    }

    /// <summary>
    ///     Called when the logical selection state changes (e.g. Select All).
    ///     Derived classes (pages) should override this to sync UI.
    /// </summary>
    protected virtual void OnSelectionStateChanged()
    {
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private async Task PlayAllSongsAsync()
    {
        await EnsureRepeatOneIsOffAsync();
        // Always use the pre-fetched full ID list for memory efficiency and consistency.
        List<Guid> ids;
        _stateLock.EnterReadLock();
        try
        {
            ids = _fullSongIdList.ToList();
        }
        finally
        {
            _stateLock.ExitReadLock();
        }
        await _playbackService.PlayAsync(ids);
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private async Task ShuffleAndPlayAllSongsAsync()
    {
        await EnsureRepeatOneIsOffAsync();
        // Always use the pre-fetched full ID list for memory efficiency and consistency.
        List<Guid> ids;
        _stateLock.EnterReadLock();
        try
        {
            ids = _fullSongIdList.ToList();
        }
        finally
        {
            _stateLock.ExitReadLock();
        }
        await _playbackService.PlayAsync(ids, 0, true);
    }

    [RelayCommand]
    private async Task PlaySongAsync(Song? song)
    {
        if (song == null) return;
        await EnsureRepeatOneIsOffAsync();

        // Always use the pre-fetched full ID list to find the song's position.
        int startIndex;
        List<Guid> ids;
        _stateLock.EnterReadLock();
        try
        {
            startIndex = _fullSongIdList.IndexOf(song.Id);
            ids = _fullSongIdList.ToList();
        }
        finally
        {
            _stateLock.ExitReadLock();
        }

        if (startIndex == -1)
        {
            // Fallback for a song not in the list for some reason.
            await _playbackService.PlayAsync(song.Id);
            return;
        }

        await _playbackService.PlayAsync(ids, startIndex);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private async Task PlaySelectedSongsAsync()
    {
        await EnsureRepeatOneIsOffAsync();
        var ids = await GetCurrentSelectionIdsAsync();
        await _playbackService.PlayAsync(ids);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private async Task PlaySelectedSongsNextAsync()
    {
        var ids = await GetCurrentSelectionIdsAsync();
        // Reverse the list so songs are added in the selected order after the current track.
        foreach (var id in ids.AsEnumerable().Reverse()) await _playbackService.PlayNextAsync(id);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private async Task AddSelectedSongsToQueueAsync()
    {
        var ids = await GetCurrentSelectionIdsAsync();
        await _playbackService.AddRangeToQueueAsync(ids);
    }

    [RelayCommand(CanExecute = nameof(CanAddSelectedSongsToPlaylist))]
    private async Task AddSelectedSongsToPlaylistAsync(Playlist? playlist)
    {
        if (playlist == null || !HasSelectedSongs) return;
        var ids = await GetCurrentSelectionIdsAsync();
        await _playlistService.AddSongsToPlaylistAsync(playlist.Id, ids);
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
    private async Task GoToAlbumAsync(object? parameter)
    {
        if (_isNavigatingToAlbum) return;

        try
        {
            _isNavigatingToAlbum = true;
            await _musicNavigationService.NavigateToAlbumAsync(parameter);
        }
        finally
        {
            _isNavigatingToAlbum = false;
        }
    }

    [RelayCommand]
    public async Task GoToArtistAsync(object? parameter)
    {
        if (_isNavigatingToArtist) return;

        try
        {
            _isNavigatingToArtist = true;

            // For the base's "Play All/Selected" context, if no parameter is provided, we use the first selected song.
            if (parameter == null && SelectedSongs.Any())
            {
                parameter = SelectedSongs.First();
            }

            await _musicNavigationService.NavigateToArtistAsync(parameter);
        }
        finally
        {
            _isNavigatingToArtist = false;
        }
    }



    protected bool CanExecuteLoadCommands()
    {
        return !IsOverallLoading;
    }

    private bool CanExecutePlayAllCommands()
    {
        // Always use the pre-fetched full ID list for consistency.
        _stateLock.EnterReadLock();
        try
        {
            return !IsOverallLoading && _fullSongIdList.Any();
        }
        finally
        {
            _stateLock.ExitReadLock();
        }
    }

    private bool CanExecuteSelectedSongsCommands()
    {
        return SelectedItemsCount > 0;
    }

    private bool CanExecuteSingleSongCommands()
    {
        return IsSingleSongSelected;
    }

    private bool CanAddSelectedSongsToPlaylist()
    {
        return CanExecuteSelectedSongsCommands() && AvailablePlaylists.Any();
    }

    /// <summary>
    ///     Processes a page of results, updating the UI-bound collection and internal paging state.
    ///     Derived classes can override to update additional collections (e.g., FolderContents).
    /// </summary>
    protected virtual void ProcessPagedResult(PagedResult<Song> pagedResult, CancellationToken token, bool append = false)
    {
        if (pagedResult?.Items == null || token.IsCancellationRequested || _isDisposed) return;

        // All UI updates must be marshalled to the UI thread.
        _dispatcherService.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested || _isDisposed) return;

            if (append)
            {
                foreach (var song in pagedResult.Items)
                    Songs.Add(song);
            }
            else
            {
                Songs = new ObservableCollection<Song>(pagedResult.Items);
            }
        });

        // Update internal state within a lock for thread safety.
        _stateLock.EnterWriteLock();
        try
        {
            _hasNextPage = pagedResult.HasNextPage;
            _totalItemCount = pagedResult.TotalCount;
            _currentPage = pagedResult.PageNumber;
        }
        finally
        {
            _stateLock.ExitWriteLock();
        }

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            TotalItemsText = pagedResult.TotalCount == 1 
                ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.SongList_TotalItems_Format_Singular, pagedResult.TotalCount) 
                : ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.SongList_TotalItems_Format_Plural, pagedResult.TotalCount);
            UpdateSelectionStatus();
            PlayAllSongsCommand.NotifyCanExecuteChanged();
            ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
        });
    }

    protected void UpdateSelectionStatus()
    {
        var count = SelectedItemsCount;
        SelectedItemsCountText = count > 0 
            ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.SongList_SelectedCount_Format, count) 
            : string.Empty;
        IsSingleSongSelected = count == 1;
        OnPropertyChanged(nameof(SelectedItemsCount));
        OnPropertyChanged(nameof(HasSelectedSongs));
        UpdateSelectionDependentCommands();
    }

    protected virtual Task<List<Guid>> GetCurrentSelectionIdsAsync()
    {
        _stateLock.EnterReadLock();
        try
        {
            return Task.FromResult(SelectionState.GetSelectedIds(_fullSongIdList).ToList());
        }
        finally
        {
            _stateLock.ExitReadLock();
        }
    }

    protected void UpdateSortOrderButtonText(SongSortOrder sortOrder)
    {
        CurrentSortOrderText = SortOrderHelper.GetDisplayName(sortOrder);
    }

    protected virtual Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return Task.CompletedTask;
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
            SongSortOrder.TitleAsc => songs.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id),
            SongSortOrder.TitleDesc => songs.OrderByDescending(s => s.Title, StringComparer.OrdinalIgnoreCase).ThenByDescending(s => s.Id),
            SongSortOrder.YearAsc => songs.OrderBy(s => s.Year)
                .ThenBy(s => s.ArtistName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Id),
            SongSortOrder.YearDesc => songs.OrderByDescending(s => s.Year)
                .ThenByDescending(s => s.ArtistName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.Id),
            SongSortOrder.AlbumAsc or SongSortOrder.TrackNumberAsc => songs.OrderBy(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Id),
            SongSortOrder.AlbumDesc or SongSortOrder.TrackNumberDesc => songs.OrderByDescending(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.Id),
            SongSortOrder.ArtistAsc => songs.OrderBy(s => s.ArtistName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Id),
            SongSortOrder.ArtistDesc => songs.OrderByDescending(s => s.ArtistName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.Id),
            _ => songs.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id)
        };
    }

    /// <summary>
    ///     Cleans up resources, particularly stopping any in-flight background loading tasks.
    ///     Doesn't dispose state lock as it's a singleton.
    /// </summary>
    public override void ResetState()
    {
        base.ResetState();
        SelectionState.DeselectAll();
        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();
        _pagedLoadCts = null;
        
        _logger.LogDebug("Reset state for SongListViewModelBase");
    }

    /// <summary>
    ///     Disposes all resources including the state lock.
    /// </summary>
    public virtual void Dispose()
    {
        if (_isDisposed) return;
        
        ResetState();
        _stateLock.Dispose();
        
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}