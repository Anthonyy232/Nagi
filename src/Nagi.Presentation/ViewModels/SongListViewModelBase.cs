using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Adds selection, playback, and infinite scrolling to song lists.
/// </summary>
public abstract partial class SongListViewModelBase : PagedListViewModelBase<Song>, INowPlayingSource
{
    protected readonly ILibraryWriter _libraryWriter;
    protected readonly IMusicPlaybackService _playbackService;
    protected readonly IPlaylistService _playlistService;
    protected readonly IUIService _uiService;
    protected readonly IMusicNavigationService _musicNavigationService;
    public bool HasLoadedPlaylists { get; private set; }

    /// <summary>
    ///     The Id of the song that is currently playing in the player, mirrored from
    ///     <see cref="IMusicPlaybackService.CurrentTrack"/> so list rows can show a
    ///     "now playing" indicator. Null when nothing is playing.
    /// </summary>
    [ObservableProperty] public partial Guid? CurrentPlayingSongId { get; set; }

    /// <summary>
    ///     Whether playback is running, mirrored from <see cref="IMusicPlaybackService.IsPlaying" />.
    /// </summary>
    [ObservableProperty] public partial bool IsPlaying { get; set; }

    // Loaded on demand unless a view requires it for editing.
    protected readonly object _fullSongIdsLock = new();
    protected List<Guid> _fullSongIdList = new();
    private Task<List<Guid>>? _fullSongIdLoadTask;
    private int _fullSongIdGeneration;

    protected virtual bool PrefetchSongIds => false;

    // Owned by the song-base's <see cref="LoadPageAsync"/> envelope (Next/Previous reload that skips
    // the auxiliary ID fetch). Distinct from the base's internal page CTS, which gates LoadAsync.
    protected CancellationTokenSource? _pagedLoadCts;

    protected SongListViewModelBase(
        ILibraryReader libraryReader,
        ILibraryWriter libraryWriter,
        ILibraryScanner libraryScanner,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger logger)
        : base(libraryReader, libraryScanner, settingsService, dispatcherService, logger)
    {
        _libraryWriter = libraryWriter;
        _playlistService = playlistService;
        _playbackService = playbackService;
        _musicNavigationService = musicNavigationService;
        _uiService = uiService;

        Songs.CollectionChanged += OnSongsCollectionChanged;
        _playlistService.PlaylistsChanged += OnPlaylistsChanged;

        // Mirror the playback service's current track Id so row "now playing" indicators
        // can react. Initialize from the current state in case a track is already playing
        // when this view model is constructed (e.g. navigating to a list while music plays).
        CurrentPlayingSongId = _playbackService.CurrentTrack?.Id;
        _playbackService.TrackChanged += OnPlaybackTrackChanged;
        IsPlaying = _playbackService.IsPlaying;
        _playbackService.PlaybackStateChanged += OnPlaybackStateChanged;
        _settingsService.SongListColumnsChanged += OnSongListColumnsChanged;
        _settingsService.ShowSongListHeaderSettingChanged += OnShowSongListHeaderChanged;
        _ = LoadListSettingsAsync();
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

    private void OnPlaybackStateChanged()
    {
        if (_isDisposed) return;
        var isPlaying = _playbackService.IsPlaying;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            IsPlaying = isPlaying;
        });
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        // Only reload if we've already loaded once; ignore if never loaded (avoids loading when not needed).
        if (!HasLoadedPlaylists || _isDisposed) return;

        _dispatcherService.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                _ = LoadAvailablePlaylistsAsync();
            }
        });
    }

    public ObservableRangeCollection<Song> Songs { get; } = new();

    private void OnSongsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    public ObservableCollection<Song> SelectedSongs { get; } = new();

    [ObservableProperty] public partial ObservableCollection<Playlist> AvailablePlaylists { get; set; } = new();

    [ObservableProperty] public partial string PageTitle { get; set; } = Strings.SongList_PageTitle_Default;

    [ObservableProperty]
    public partial string SelectedItemsCountText { get; set; } = string.Empty;

    public virtual int SelectedItemsCount => SelectedSongs.Count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSortOrderText))]
    public partial SongSortOrder CurrentSortOrder { get; set; } = SongSortOrder.TitleAsc;

    partial void OnCurrentSortOrderChanged(SongSortOrder oldValue, SongSortOrder newValue)
    {
        OnCurrentSortOrderChangedInternal(oldValue, newValue);
    }

    protected virtual void OnCurrentSortOrderChangedInternal(SongSortOrder oldOrder, SongSortOrder newOrder)
    {
        ClearFullSongIds();
    }

    public string CurrentSortOrderText => SortOrderHelper.GetDisplayName(CurrentSortOrder);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleColumns))]
    public partial SongListColumns ChosenColumns { get; set; } = SettingsDefaults.DefaultSongListColumns;

    [ObservableProperty] public partial bool ShowsColumnHeader { get; set; } = SettingsDefaults.ShowSongListHeaderEnabled;

    /// <summary>
    ///     The chosen columns, less any that every row would repeat, such as the album on an album's page.
    /// </summary>
    public virtual SongListColumns VisibleColumns => ChosenColumns;

    private async Task LoadListSettingsAsync()
    {
        ChosenColumns = await _settingsService.GetSongListColumnsAsync();
        ShowsColumnHeader = await _settingsService.GetShowSongListHeaderAsync();
    }

    private void OnSongListColumnsChanged(SongListColumns columns)
    {
        if (_isDisposed) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (!_isDisposed) ChosenColumns = columns;
        });
    }

    private void OnShowSongListHeaderChanged(bool isEnabled)
    {
        if (_isDisposed) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (!_isDisposed) ShowsColumnHeader = isEnabled;
        });
    }

    public bool ShowEmptyState => !IsLoading && Songs.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowInFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSongInfoCommand))]
    public partial bool IsSingleSongSelected { get; set; }

    public bool HasSelectedSongs => SelectedItemsCount > 0;

    protected override bool ShouldReloadOnLibraryChange(LibraryChangeType changeType) => changeType == LibraryChangeType.SongsUpdated;

    protected override void OnLoadingStateChanged(bool isLoading)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        RefreshOrSortSongsCommand.NotifyCanExecuteChanged();
        PlayAllSongsCommand.NotifyCanExecuteChanged();
        ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
        PlayMixCommand.NotifyCanExecuteChanged();
        PlayForCommand.NotifyCanExecuteChanged();
    }

    protected override void OnSearchTermChangedInternal(string value)
    {
        base.OnSearchTermChangedInternal(value);
        _pagedLoadCts?.Cancel();
        if (HasSelectedSongs) DeselectAll();
        ClearFullSongIds();
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

    /// <summary>Refreshes the playback ID cache when a view requires it.</summary>
    protected override async Task OnAuxiliaryLoadAsync(CancellationToken token)
    {
        var generation = ClearFullSongIds();
        if (IsSearchActive || !PrefetchSongIds)
        {
            return;
        }

        var ids = await LoadAllSongIdsAsync(CurrentSortOrder, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        StoreFullSongIds(ids, generation);
    }

    protected int ClearFullSongIds()
    {
        lock (_fullSongIdsLock)
        {
            _fullSongIdList = new List<Guid>();
            _fullSongIdLoadTask = null;
            return ++_fullSongIdGeneration;
        }
    }

    private void StoreFullSongIds(List<Guid> ids, int generation)
    {
        lock (_fullSongIdsLock)
        {
            if (_isDisposed || generation != _fullSongIdGeneration) return;
            _fullSongIdList = ids;
            _fullSongIdLoadTask = Task.FromResult(ids);
        }
    }

    protected async Task<List<Guid>> GetFullSongIdsAsync()
    {
        Task<List<Guid>> loadTask;
        int generation;

        lock (_fullSongIdsLock)
        {
            if (_isDisposed) return new List<Guid>();

            generation = _fullSongIdGeneration;
            var sortOrder = CurrentSortOrder;
            loadTask = _fullSongIdLoadTask ??=
                Task.Run(() => LoadAllSongIdsAsync(sortOrder, CancellationToken.None));
        }

        try
        {
            var ids = await loadTask.ConfigureAwait(false);
            lock (_fullSongIdsLock)
            {
                if (_isDisposed || generation != _fullSongIdGeneration)
                    return new List<Guid>();

                if (ReferenceEquals(_fullSongIdLoadTask, loadTask))
                    _fullSongIdList = ids;

                return _fullSongIdList.ToList();
            }
        }
        catch
        {
            lock (_fullSongIdsLock)
            {
                if (_isDisposed || generation != _fullSongIdGeneration)
                    return [];
                if (ReferenceEquals(_fullSongIdLoadTask, loadTask))
                    _fullSongIdLoadTask = null;
            }

            throw;
        }
    }

    protected int CaptureSongContext()
    {
        lock (_fullSongIdsLock) return _fullSongIdGeneration;
    }

    protected bool IsSongContextCurrent(int generation)
    {
        lock (_fullSongIdsLock) return !_isDisposed && generation == _fullSongIdGeneration;
    }

    /// <summary>Songs use the SongList resource strings rather than per-list-type formatting.</summary>
    protected override string FormatTotalItemsText(int count) =>
        count == 1
            ? ResourceFormatter.Format(Strings.SongList_TotalItems_Format_Singular, count)
            : ResourceFormatter.Format(Strings.SongList_TotalItems_Format_Plural, count);

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
            try { await SaveSortOrderAsync(newSortOrder); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to save sort order"); }
        }

        UpdateSelectionStatus();

        await LoadAsync(manualToken);
    }

    /// <summary>
    ///     Loads a specific page of songs without re-fetching all song IDs.
    ///     Useful for pagination where the overall sort order and ID list remain unchanged.
    /// </summary>
    public virtual async Task LoadPageAsync(int pageNumber)
    {
        lock (_loadLock)
        {
            if (_isDisposed || IsLoading || !IsPaginationEnabled) return;
            IsLoading = true;
        }

        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();
        _pagedLoadCts = new CancellationTokenSource();
        var token = _pagedLoadCts.Token;
        HasLoadError = false;

        try
        {
            var pagedResult = await Task.Run(
                () => LoadValidPageAsync(pageNumber, PageSize, token), token);
            ProcessPagedResult(pagedResult, token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Song page load was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load next page");
            if (!token.IsCancellationRequested && !_isDisposed) HasLoadError = true;
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
        Songs.AppendOrReplace(result.Items, append);
        UpdateSelectionStatus();
        PlayAllSongsCommand.NotifyCanExecuteChanged();
        ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
        PlayMixCommand.NotifyCanExecuteChanged();
        PlayForCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadAvailablePlaylistsAsync()
    {
        try
        {
            var playlists = await _libraryReader.GetAllPlaylistsAsync();
            if (_isDisposed) return;
            AvailablePlaylists = new ObservableCollection<Playlist>(playlists);
            HasLoadedPlaylists = true;
            AddSelectedSongsToPlaylistCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load available playlists");
        }
    }

    /// <summary>
    ///     Mirrors the rows selected in the list.
    /// </summary>
    public virtual void OnSongsSelectionChanged(IEnumerable<object> selectedItems)
    {
        SelectedSongs.Clear();
        foreach (var item in selectedItems.OfType<Song>()) SelectedSongs.Add(item);
        UpdateSelectionStatus();
    }

    public virtual void DeselectAll()
    {
        SelectedSongs.Clear();
        UpdateSelectionStatus();
    }

    /// <summary>
    ///     Returns the playback context for this view. Subclasses override to provide the correct source type and ID.
    /// </summary>
    protected virtual PlaybackContext GetPlaybackContext() => new(PlaybackContextType.Library, null);

    /// <summary>
    ///     Runs a user action, logging a failure and telling the user instead of letting it reach the view.
    /// </summary>
    protected Task RunActionAsync(Func<Task> action) => UserAction.RunAsync(action, _logger, _uiService, () => _isDisposed);

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private Task PlayAllSongsAsync() => RunActionAsync(async () =>
    {
        var context = GetPlaybackContext();
        var ids = await GetFullSongIdsAsync();
        if (ids.Count == 0) return;
        await _playbackService.PlayAsync(ids, 0, null, context);
    });

    [RelayCommand(CanExecute = nameof(CanExecutePlayAllCommands))]
    private Task ShuffleAndPlayAllSongsAsync() => RunActionAsync(async () =>
    {
        var context = GetPlaybackContext();
        var ids = await GetFullSongIdsAsync();
        if (ids.Count == 0) return;
        await _playbackService.PlayAsync(ids, 0, true, context);
    });

    [RelayCommand]
    private Task PlaySongAsync(Song? song) => song is null ? Task.CompletedTask : RunActionAsync(async () =>
    {
        var generation = CaptureSongContext();
        var context = GetPlaybackContext();
        var ids = await GetFullSongIdsAsync();
        if (!IsSongContextCurrent(generation)) return;
        var startIndex = ids.IndexOf(song.Id);

        if (startIndex == -1)
        {
            // Fallback for a song not in the list for some reason.
            await _playbackService.PlayAsync(song.Id);
            return;
        }

        await _playbackService.PlayAsync(ids, startIndex, null, context);
    });

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private Task PlaySelectedSongsAsync() => RunActionAsync(async () =>
    {
        var context = GetPlaybackContext();
        var ids = await GetCurrentSelectionIdsAsync();
        if (ids.Count == 0) return;
        await _playbackService.PlayAsync(ids, 0, null, context);
    });

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private Task PlaySelectedSongsNextAsync() => RunActionAsync(async () =>
    {
        var ids = await GetCurrentSelectionIdsAsync();
        // Reverse the list so songs are added in the selected order after the current track.
        foreach (var id in ids.AsEnumerable().Reverse()) await _playbackService.PlayNextAsync(id);
    });

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedSongsCommands))]
    private Task AddSelectedSongsToQueueAsync() => RunActionAsync(async () =>
    {
        var ids = await GetCurrentSelectionIdsAsync();
        if (ids.Count == 0) return;
        await _playbackService.AddRangeToQueueAsync(ids);
    });

    [RelayCommand(CanExecute = nameof(CanAddSelectedSongsToPlaylist))]
    private Task AddSelectedSongsToPlaylistAsync(Playlist? playlist) =>
        playlist is null || !HasSelectedSongs ? Task.CompletedTask : RunActionAsync(async () =>
        {
            var ids = await GetCurrentSelectionIdsAsync();
            if (ids.Count == 0) return;
            await _playlistService.AddSongsToPlaylistAsync(playlist.Id, ids);
        });

    [RelayCommand(CanExecute = nameof(CanExecuteSingleSongCommands))]
    private async Task ShowInFolderAsync(Song? song)
    {
        var targetSong = song ?? SelectedSongs.FirstOrDefault();
        if (targetSong?.FilePath is null) return;
        await _uiService.ShowInFolderAsync(targetSong.FilePath);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSingleSongCommands))]
    private async Task ShowSongInfoAsync(Song? song)
    {
        if ((song ?? SelectedSongs.FirstOrDefault()) is { } targetSong)
            await _uiService.ShowSongInfoAsync(targetSong);
    }

    [RelayCommand]
    private Task GoToAlbumAsync(object? parameter) => _musicNavigationService.NavigateToAlbumAsync(parameter);

    /// <summary>
    ///     Loads a song's credited artists in credit order, for the "Go to artist" submenu.
    /// </summary>
    /// <returns>The artists, or <c>null</c> if they couldn't be loaded.</returns>
    public async Task<IReadOnlyList<Artist>?> GetArtistsForSongAsync(Song song)
    {
        try
        {
            return (await _libraryReader.GetArtistsForSongAsync(song.Id).ConfigureAwait(false)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the artists of song {SongId}", song.Id);
            return null;
        }
    }

    [RelayCommand]
    // Without a parameter it opens the artist of the selected song.
    public Task GoToArtistAsync(object? parameter) =>
        _musicNavigationService.NavigateToArtistAsync(parameter ?? SelectedSongs.FirstOrDefault());

    protected bool CanExecuteLoadCommands()
    {
        return !IsLoading;
    }

    /// <summary>Whether play all, shuffle and mixes have anything to play.</summary>
    protected virtual bool HasPlayableSongs => Songs.Any();

    private bool CanExecutePlayAllCommands() => !IsLoading && HasPlayableSongs;

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

    protected virtual void UpdateSelectionStatus()
    {
        var count = SelectedItemsCount;
        SelectedItemsCountText = count > 0
            ? ResourceFormatter.Format(Strings.SongList_SelectedCount_Format, count)
            : string.Empty;
        IsSingleSongSelected = count == 1;
        OnPropertyChanged(nameof(SelectedItemsCount));
        OnPropertyChanged(nameof(HasSelectedSongs));
        UpdateSelectionDependentCommands();
    }

    /// <summary>
    ///     The selected songs in list order, so playing or queueing a selection keeps the order shown.
    /// </summary>
    protected virtual Task<List<Guid>> GetCurrentSelectionIdsAsync()
    {
        var selected = SelectedSongs.Select(song => song.Id).ToHashSet();
        return Task.FromResult(Songs.Where(song => selected.Contains(song.Id)).Select(song => song.Id).ToList());
    }

    protected virtual Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return Task.CompletedTask;
    }

    private void UpdateSelectionDependentCommands()
    {
        PlaySelectedSongsCommand.NotifyCanExecuteChanged();
        PlaySelectedSongsNextCommand.NotifyCanExecuteChanged();
        AddSelectedSongsToQueueCommand.NotifyCanExecuteChanged();
        AddSelectedSongsToPlaylistCommand.NotifyCanExecuteChanged();
        SetSelectedFavoritesCommand.NotifyCanExecuteChanged();
        ToggleSelectedFavoriteCommand.NotifyCanExecuteChanged();
    }

    public override void ResetState()
    {
        base.ResetState();
        DeselectAll();
        ClearFullSongIds();
        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();
        _pagedLoadCts = null;

        _logger.LogDebug("Reset state for SongListViewModelBase");
    }

    public override void Dispose()
    {
        if (_isDisposed) return;

        ResetState();

        _playlistService.PlaylistsChanged -= OnPlaylistsChanged;
        _playbackService.TrackChanged -= OnPlaybackTrackChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _settingsService.SongListColumnsChanged -= OnSongListColumnsChanged;
        _settingsService.ShowSongListHeaderSettingChanged -= OnShowSongListHeaderChanged;
        Songs.CollectionChanged -= OnSongsCollectionChanged;

        base.Dispose();

    }
}
