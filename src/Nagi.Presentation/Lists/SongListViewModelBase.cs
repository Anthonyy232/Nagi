using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Settings;

namespace Nagi.Presentation.Lists;

/// <summary>
///     Adds selection, playback, and infinite scrolling to song lists.
/// </summary>
public abstract partial class SongListViewModelBase : PagedListViewModelBase<Song>
{
    protected readonly IListeningService _listening;
    protected readonly IMusicPlaybackService _playbackService;
    protected readonly IPlaylistService _playlistService;
    protected readonly IUIService _uiService;
    protected readonly IMusicNavigationService _musicNavigationService;
    public bool HasLoadedPlaylists { get; private set; }

    // Loaded on demand unless a view requires it for editing.
    private readonly object _fullSongIdsLock = new();
    private List<Guid> _fullSongIdList = new();
    private Task<List<Guid>>? _fullSongIdLoadTask;
    private int _fullSongIdGeneration;

    protected virtual bool PrefetchSongIds => false;

    protected SongListViewModelBase(
        ILibraryReader libraryReader,
        IListeningService listening,
        ILibraryNotifier libraryNotifier,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        ISettingsService settingsService,
        IUIService uiService,
        ILogger logger,
        IReadOnlyList<SongSortOrder> sortOrders,
        Setting<SongSortOrder>? sortOrderSetting)
        : base(libraryReader, libraryNotifier, settingsService, dispatcherService, logger)
    {
        Sort = sortOrderSetting is null
            ? new SortOrderSelection<SongSortOrder>(sortOrders, sortOrders[0], SaveSortOrderAsync, OnSortOrderChangedAsync, logger)
            : new SortOrderSelection<SongSortOrder>(sortOrders, sortOrderSetting, settingsService, OnSortOrderChangedAsync, logger);
        _listening = listening;
        _playlistService = playlistService;
        _playbackService = playbackService;
        _musicNavigationService = musicNavigationService;
        _uiService = uiService;

        _playlistService.PlaylistsChanged += OnPlaylistsChanged;

        ChosenColumns = _settingsService.Get(AppSettings.SongListColumns);
        ShowsColumnHeader = _settingsService.Get(AppSettings.ShowSongListHeader);
        _settingsService.SettingChanged += OnSettingChanged;
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
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

    public ObservableCollection<Song> SelectedSongs { get; } = new();

    [ObservableProperty] public partial ObservableCollection<Playlist> AvailablePlaylists { get; set; } = new();

    [ObservableProperty] public partial string PageTitle { get; set; } = Strings.SongList_Songs;

    [ObservableProperty]
    public partial string SelectedItemsCountText { get; set; } = string.Empty;

    public virtual int SelectedItemsCount => SelectedSongs.Count;

    /// <summary>The orders most song lists offer.</summary>
    protected static readonly SongSortOrder[] CommonSortOrders =
    [
        SongSortOrder.TitleAsc, SongSortOrder.TitleDesc, SongSortOrder.YearAsc, SongSortOrder.YearDesc,
        SongSortOrder.ArtistAsc, SongSortOrder.ArtistDesc, SongSortOrder.AlbumAsc, SongSortOrder.AlbumDesc,
        SongSortOrder.FileCreatedDateAsc, SongSortOrder.FileCreatedDateDesc
    ];

    public SortOrderSelection<SongSortOrder> Sort { get; }

    protected virtual Task OnSortOrderChangedAsync()
    {
        ClearFullSongIds();
        CurrentPage = 1;
        return LoadAsync(CancellationToken.None);
    }

    /// <summary>Saves a chosen order for a list whose order isn't kept in a setting.</summary>
    protected virtual Task SaveSortOrderAsync(SongSortOrder sortOrder) => Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleColumns))]
    public partial SongListColumns ChosenColumns { get; set; }

    [ObservableProperty] public partial bool ShowsColumnHeader { get; set; }

    /// <summary>
    ///     The chosen columns, less any that every row would repeat, such as the album on an album's page.
    /// </summary>
    public virtual SongListColumns VisibleColumns => ChosenColumns;

    /// <summary>Whether a song's menu offers its album; not on the album's own page.</summary>
    public virtual bool ShowsGoToAlbum => true;

    /// <summary>Whether a song's menu offers its artists; not on an artist's own page.</summary>
    public virtual bool ShowsGoToArtist => true;

    /// <summary>Removes the selected songs from this list, for a list the user can remove songs from.</summary>
    public virtual ICommand? RemoveSelectionCommand => null;

    private void OnSettingChanged(Setting setting)
    {
        if (setting != AppSettings.SongListColumns && setting != AppSettings.ShowSongListHeader) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            ChosenColumns = _settingsService.Get(AppSettings.SongListColumns);
            ShowsColumnHeader = _settingsService.Get(AppSettings.ShowSongListHeader);
        });
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowInFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSongInfoCommand))]
    public partial bool IsSingleSongSelected { get; set; }

    public bool HasSelectedSongs => SelectedItemsCount > 0;

    protected override bool ShouldReloadOnLibraryChange(LibraryChange change) =>
        change.AffectsSongs || change.AffectsFavorites;

    protected override void OnLoadingStateChanged(bool isLoading)
    {
        NotifyPlayCommandsChanged();
    }

    private void NotifyPlayCommandsChanged()
    {
        PlayAllSongsCommand.NotifyCanExecuteChanged();
        ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
        PlayMixCommand.NotifyCanExecuteChanged();
        PlayForCommand.NotifyCanExecuteChanged();
    }

    protected override void OnSearchTermChangedInternal(string value)
    {
        base.OnSearchTermChangedInternal(value);
        if (HasSelectedSongs) DeselectAll();
        ClearFullSongIds();
    }

    protected sealed override Task<PagedResult<Song>> LoadPageItemsAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        return LoadSongsPagedAsync(pageNumber, pageSize, Sort.Current, token);
    }

    protected override async Task OnAuxiliaryLoadAsync(CancellationToken token)
    {
        var generation = ClearFullSongIds();
        if (IsSearchActive || !PrefetchSongIds)
        {
            return;
        }

        var ids = await LoadAllSongIdsAsync(Sort.Current, token).ConfigureAwait(false);
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
            var sortOrder = Sort.Current;
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

    /// <summary>
    ///     Replaces the cached play-all IDs with the order the user has just given the list.
    /// </summary>
    /// <returns>False when the cache already holds that order.</returns>
    protected bool ReplaceCachedSongIds(IReadOnlyList<Guid> ids)
    {
        lock (_fullSongIdsLock)
        {
            if (_fullSongIdList.SequenceEqual(ids)) return false;
            _fullSongIdList = ids.ToList();
            _fullSongIdLoadTask = Task.FromResult(_fullSongIdList);
            return true;
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

    protected override string FormatTotalItemsText(PagedResult<Song> result) =>
        ResourceFormatter.Format(Strings.SongList_TotalItems_Format, result.TotalCount);

    /// <summary>The songs this list shows, or <c>null</c> while nothing is open.</summary>
    protected virtual SongScope? Scope => null;

    protected virtual Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder,
        CancellationToken cancellationToken = default) =>
        Scope is { } scope
            ? _libraryReader.GetSongsAsync(scope, SearchTerm, sortOrder, pageNumber, pageSize, cancellationToken)
            : Task.FromResult(new PagedResult<Song>());

    protected virtual Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default) =>
        Scope is { } scope
            ? _libraryReader.GetSongIdsAsync(scope, SearchTerm, sortOrder, token)
            : Task.FromResult(new List<Guid>());

    protected override void ApplyItemsToCollection(PagedResult<Song> result, bool append)
    {
        Songs.AppendOrReplace(result.Items, append);
        UpdateSelectionStatus();
        NotifyPlayCommandsChanged();
    }

    public async Task LoadAvailablePlaylistsAsync()
    {
        try
        {
            var playlists = await _playlistService.GetAllPlaylistsAsync();
            if (_isDisposed) return;
            // A server's playlists follow the server, so songs can't be added to them here.
            AvailablePlaylists = new ObservableCollection<Playlist>(playlists.Where(playlist => !playlist.IsFromServer));
            HasLoadedPlaylists = true;
            AddSelectedSongsToPlaylistCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load available playlists");
        }
    }

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

    protected virtual PlaybackContext GetPlaybackContext() =>
        Scope is SongScope.LibraryScope or SongScope.FavoritesScope && IsSearchActive
            ? new(PlaybackContextType.Search, null)
            : PlaybackContext.Of(Scope ?? SongScope.Library);

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
        await _playbackService.PlayNextAsync(await GetCurrentSelectionIdsAsync());
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

    [RelayCommand(CanExecute = nameof(CanShowInFolder))]
    private async Task ShowInFolderAsync(Song? song)
    {
        if ((song ?? SelectedSongs.FirstOrDefault()) is { IsFromServer: false } targetSong)
            await _uiService.ShowInFolderAsync(targetSong.FilePath);
    }

    private bool CanShowInFolder(Song? song) =>
        IsSingleSongSelected && (song ?? SelectedSongs.FirstOrDefault()) is { IsFromServer: false };

    [RelayCommand(CanExecute = nameof(CanExecuteSingleSongCommands))]
    private async Task ShowSongInfoAsync(Song? song)
    {
        if ((song ?? SelectedSongs.FirstOrDefault()) is { } targetSong)
            await _uiService.ShowSongInfoAsync(targetSong);
    }

    [RelayCommand]
    private Task GoToAlbumAsync(Song? song) => song is null ? Task.CompletedTask : _musicNavigationService.OpenAlbumAsync(song);

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
    private Task GoToArtistAsync(ArtistNavigationRequest? request) =>
        request is null ? Task.CompletedTask : _musicNavigationService.OpenArtistAsync(request);

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
    }

    public override void Dispose()
    {
        if (_isDisposed) return;

        ResetState();

        _playlistService.PlaylistsChanged -= OnPlaylistsChanged;
        _settingsService.SettingChanged -= OnSettingChanged;

        base.Dispose();
    }
}
