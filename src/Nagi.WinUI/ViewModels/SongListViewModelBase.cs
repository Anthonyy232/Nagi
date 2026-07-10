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
///     Base class for song list view models. Adds selection, playback commands, the
///     full ID list (used for "Play All" without holding every <see cref="Song"/> in
///     memory), and infinite-scroll auto-loading when
///     <see cref="PagedListViewModelBase{TItem}.IsPaginationEnabled"/> is false.
/// </summary>
public abstract partial class SongListViewModelBase : PagedListViewModelBase<Song>
{
    protected readonly ILibraryReader _libraryReader;
    protected readonly INavigationService _navigationService;
    protected readonly IMusicPlaybackService _playbackService;
    protected readonly IPlaylistService _playlistService;
    protected readonly ReaderWriterLockSlim _stateLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly IUIService _uiService;
    protected readonly IMusicNavigationService _musicNavigationService;
    private bool _hasLoadedPlaylists;

    public bool HasLoadedPlaylists => _hasLoadedPlaylists;

    /// <summary>
    ///     The Id of the song that is currently playing in the player, mirrored from
    ///     <see cref="IMusicPlaybackService.CurrentTrack"/> so list rows can show a
    ///     "now playing" indicator. Null when nothing is playing.
    /// </summary>
    [ObservableProperty] public partial Guid? CurrentPlayingSongId { get; set; }

    // For paged views, this holds all song IDs to enable "Play All" without loading all song objects into memory.
    protected List<Guid> _fullSongIdList = new();

    // Owned by the song-base's <see cref="LoadPageAsync"/> envelope (Next/Previous reload that skips
    // the auxiliary ID fetch). Distinct from the base's internal page CTS, which gates LoadAsync.
    protected CancellationTokenSource? _pagedLoadCts;

    // Navigation re-entry guards
    private bool _isNavigatingToArtist;
    private bool _isNavigatingToAlbum;

    /// <summary>
    ///     Gets the logical selection state for this view.
    /// </summary>
    public SelectionState SelectionState { get; } = new();

    protected SongListViewModelBase(
        ILibraryService libraryService,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger logger)
        : base(libraryService, settingsService, dispatcherService, logger)
    {
        _libraryReader = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
        _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _musicNavigationService = musicNavigationService ?? throw new ArgumentNullException(nameof(musicNavigationService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));

        Songs.CollectionChanged += OnSongsCollectionChanged;
        _playlistService.PlaylistsChanged += OnPlaylistsChanged;

        // Mirror the playback service's current track Id so row "now playing" indicators
        // can react. Initialize from the current state in case a track is already playing
        // when this view model is constructed (e.g. navigating to a list while music plays).
        CurrentPlayingSongId = _playbackService.CurrentTrack?.Id;
        _playbackService.TrackChanged += OnPlaybackTrackChanged;

        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    private void OnPlaybackTrackChanged()
    {
        if (_isDisposed) return;
        var newId = _playbackService.CurrentTrack?.Id;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            CurrentPlayingSongId = newId;
        });
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        // Only reload if we've already loaded once; ignore if never loaded (avoids loading when not needed).
        if (!_hasLoadedPlaylists || _isDisposed) return;

        _dispatcherService.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                _ = LoadAvailablePlaylistsAsync();
            }
        });
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirstSong))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial ObservableRangeCollection<Song> Songs { get; set; } = new();

    public Song? FirstSong => Songs?.FirstOrDefault();

    partial void OnSongsChanged(ObservableRangeCollection<Song> oldValue, ObservableRangeCollection<Song> newValue)
    {
        OnSongsChangedInternal(oldValue, newValue);
    }

    protected virtual void OnSongsChangedInternal(ObservableRangeCollection<Song> oldValue, ObservableRangeCollection<Song> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= OnSongsCollectionChanged;
        if (newValue != null) newValue.CollectionChanged += OnSongsCollectionChanged;
    }

    private void OnSongsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    [ObservableProperty] public partial ObservableCollection<Song> SelectedSongs { get; set; } = new();

    [ObservableProperty] public partial ObservableCollection<Playlist> AvailablePlaylists { get; set; } = new();

    [ObservableProperty] public partial string PageTitle { get; set; } = Nagi.WinUI.Resources.Strings.SongList_PageTitle_Default;


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

    public bool ShowEmptyState => !IsLoading && Songs.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowInFileExplorerCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToAlbumCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToArtistCommand))]
    public partial bool IsSingleSongSelected { get; set; }

    public bool HasSelectedSongs => SelectedItemsCount > 0;

    // LibraryViewModel installs its own override; the other six song subclasses don't react
    // to library changes. The base's debouncer subscription is harmless either way.
    protected override void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
    }

    protected override void OnLoadingStateChanged(bool isLoading)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        RefreshOrSortSongsCommand.NotifyCanExecuteChanged();
        PlayAllSongsCommand.NotifyCanExecuteChanged();
        ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
    }

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

    /// <summary>Bridge the base's typed page hook to the song-specific virtual.</summary>
    protected sealed override Task<PagedResult<Song>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        return LoadSongsPagedAsync(pageNumber, pageSize, CurrentSortOrder, token);
    }

    /// <summary>Run the full ID fetch (used by Play All) in parallel with the page query.</summary>
    protected override async Task OnAuxiliaryLoadAsync(CancellationToken token)
    {
        var ids = await LoadAllSongIdsAsync(CurrentSortOrder, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _stateLock.EnterWriteLock();
        try { _fullSongIdList = ids; }
        finally { _stateLock.ExitWriteLock(); }
    }

    /// <summary>
    ///     After a successful page load, kick off the infinite-scroll auto-loader if
    ///     <see cref="PagedListViewModelBase{TItem}.IsPaginationEnabled"/> is false and there are
    ///     more pages. Pagination-enabled lists do nothing here.
    /// </summary>
    protected override Task OnPageLoadedAsync(PagedResult<Song> result, CancellationToken token)
    {
        if (!IsPaginationEnabled && result is not null && result.HasNextPage && !token.IsCancellationRequested)
        {
            _ = StartAutomaticPagedLoadingAsync(result.PageNumber + 1, token);
        }
        return Task.CompletedTask;
    }

    /// <summary>Songs use the SongList resource strings rather than per-list-type formatting.</summary>
    protected override string FormatTotalItemsText(int count) =>
        count == 1
            ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.SongList_TotalItems_Format_Singular, count)
            : ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.SongList_TotalItems_Format_Plural, count);

    protected virtual Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PagedResult<Song>());
    }

    protected virtual Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        return Task.FromResult(new List<Guid>());
    }

    [RelayCommand(CanExecute = nameof(CanExecuteLoadCommands))]
    public virtual async Task RefreshOrSortSongsAsync(string? sortOrderString = null, CancellationToken manualToken = default)
    {
        if (!string.IsNullOrEmpty(sortOrderString) &&
            Enum.TryParse<SongSortOrder>(sortOrderString, true, out var newSortOrder)
            && newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            CurrentPage = 1;
            _logger.LogDebug("Sort order changed to '{SortOrder}'", CurrentSortOrder);
            _ = SaveSortOrderAsync(newSortOrder)
                .ContinueWith(t => _logger.LogError(t.Exception, "Failed to save sort order"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        UpdateSortOrderButtonText(CurrentSortOrder);
        UpdateSelectionStatus();

        await LoadAsync(manualToken);
    }

    /// <summary>
    ///     Transparently loads subsequent pages in the background to create a smooth "infinite scroll" experience.
    /// </summary>
    private async Task StartAutomaticPagedLoadingAsync(int nextPageToLoad, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(100, token);
                if (token.IsCancellationRequested) break;

                var pagedResult = await LoadSongsPagedAsync(nextPageToLoad, SongsPerPage, CurrentSortOrder, token);
                if (pagedResult == null) break;

                ProcessPagedResult(pagedResult, token, true);

                if (!pagedResult.HasNextPage) break;
                nextPageToLoad++;
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
    }

    /// <summary>
    ///     Loads a specific page of songs without re-fetching all song IDs.
    ///     Useful for pagination where the overall sort order and ID list remain unchanged.
    /// </summary>
    public virtual async Task LoadPageAsync(int pageNumber)
    {
        lock (_loadLock)
        {
            if (IsLoading || !IsPaginationEnabled) return;
            IsLoading = true;
        }

        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();

        try
        {
            _pagedLoadCts = new CancellationTokenSource();
            var token = _pagedLoadCts.Token;

            var pagedResult = await LoadSongsPagedAsync(pageNumber, SongsPerPage, CurrentSortOrder, token);
            ProcessPagedResult(pagedResult, token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load next page");
        }
        finally
        {
            EndLoadAndDrainPendingReload();
        }
    }

    /// <summary>
    ///     Next/Previous step over the current page only — skip the full ID-list refetch
    ///     because the sort order and result set haven't changed across the step.
    /// </summary>
    protected override Task ReloadCurrentPageAsync(CancellationToken cancellationToken) => LoadPageAsync(CurrentPage);

    protected override void ApplyItemsToCollection(PagedResult<Song> result, bool append)
    {
        if (result?.Items == null) return;
        Songs.AppendOrReplace(result.Items, append);
        UpdateSelectionStatus();
        PlayAllSongsCommand.NotifyCanExecuteChanged();
        ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadAvailablePlaylistsAsync()
    {
        try
        {
            var playlists = await _libraryReader.GetAllPlaylistsAsync();
            AvailablePlaylists = new ObservableCollection<Playlist>(playlists);
            _hasLoadedPlaylists = true;
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

    /// <summary>
    ///     Returns the playback context for this view. Subclasses override to provide the correct source type and ID.
    /// </summary>
    protected virtual PlaybackContext GetPlaybackContext() => new(PlaybackContextType.Library, null);

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private async Task PlayAllSongsAsync()
    {
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
        await _playbackService.PlayAsync(ids, 0, null, GetPlaybackContext());
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private async Task ShuffleAndPlayAllSongsAsync()
    {
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
        await _playbackService.PlayAsync(ids, 0, true, GetPlaybackContext());
    }

    [RelayCommand]
    private async Task PlaySongAsync(Song? song)
    {
        if (song == null) return;

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

        await _playbackService.PlayAsync(ids, startIndex, null, GetPlaybackContext());
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private async Task PlaySelectedSongsAsync()
    {
        var ids = await GetCurrentSelectionIdsAsync();
        await _playbackService.PlayAsync(ids, 0, null, GetPlaybackContext());
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
        return !IsLoading;
    }

    private bool CanExecutePlayAllCommands()
    {
        // Always use the pre-fetched full ID list for consistency.
        _stateLock.EnterReadLock();
        try
        {
            return !IsLoading && _fullSongIdList.Any();
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



    protected static IEnumerable<Song> SortSongs(IEnumerable<Song> songs, SongSortOrder sortOrder)
    {
        return sortOrder switch
        {
            SongSortOrder.TitleAsc => songs.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.ArtistName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id),
            SongSortOrder.TitleDesc => songs.OrderByDescending(s => s.Title, StringComparer.OrdinalIgnoreCase).ThenByDescending(s => s.ArtistName, StringComparer.OrdinalIgnoreCase).ThenByDescending(s => s.Id),
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
            SongSortOrder.PlayCountAsc => OrderByMetric(s => s.PlayCount),
            SongSortOrder.PlayCountDesc => OrderByMetricDescending(s => s.PlayCount),
            SongSortOrder.LastPlayedAsc => OrderByMetric(s => s.LastPlayedDate),
            SongSortOrder.LastPlayedDesc => OrderByMetricDescending(s => s.LastPlayedDate),
            SongSortOrder.DateAddedAsc => OrderByMetric(s => s.DateAddedToLibrary),
            SongSortOrder.DateAddedDesc => OrderByMetricDescending(s => s.DateAddedToLibrary),
            SongSortOrder.DurationAsc => OrderByMetric(s => s.DurationTicks),
            SongSortOrder.DurationDesc => OrderByMetricDescending(s => s.DurationTicks),
            SongSortOrder.BpmAsc => OrderByMetric(s => s.Bpm ?? 0),
            SongSortOrder.BpmDesc => OrderByMetricDescending(s => s.Bpm ?? 0),
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
            SongSortOrder.FileCreatedDateAsc => songs.OrderBy(s => s.FileCreatedDate)
                .ThenBy(s => s.ArtistName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.DiscNumber ?? 0)
                .ThenBy(s => s.TrackNumber)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Id),
            SongSortOrder.FileCreatedDateDesc => songs.OrderByDescending(s => s.FileCreatedDate)
                .ThenByDescending(s => s.ArtistName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.Album?.Title, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.DiscNumber ?? 0)
                .ThenByDescending(s => s.TrackNumber)
                .ThenByDescending(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.Id),
            SongSortOrder.Random => songs.OrderBy(_ => Guid.NewGuid()),
            _ => songs.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.ArtistName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id)
        };

        IOrderedEnumerable<Song> OrderByMetric<TKey>(Func<Song, TKey> keySelector) =>
            songs.OrderBy(keySelector)
                .ThenBy(s => s.ArtistName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Id);

        IOrderedEnumerable<Song> OrderByMetricDescending<TKey>(Func<Song, TKey> keySelector) =>
            songs.OrderByDescending(keySelector)
                .ThenByDescending(s => s.ArtistName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.Id);
    }

    /// <summary>
    ///     Cancels in-flight page loads and clears selection state. Doesn't dispose
    ///     <see cref="_stateLock"/> — that lives until <see cref="Dispose"/>.
    /// </summary>
    public override void ResetState()
    {
        base.ResetState();
        SelectionState.DeselectAll();
        CancelInflightPageLoad();
        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();
        _pagedLoadCts = null;

        _logger.LogDebug("Reset state for SongListViewModelBase");
    }

    public override void Dispose()
    {
        if (_isDisposed) return;

        ResetState();
        _stateLock.Dispose();

        _playlistService.PlaylistsChanged -= OnPlaylistsChanged;
        _playbackService.TrackChanged -= OnPlaybackTrackChanged;
        Songs.CollectionChanged -= OnSongsCollectionChanged;

        base.Dispose();

        GC.SuppressFinalize(this);
    }
}
