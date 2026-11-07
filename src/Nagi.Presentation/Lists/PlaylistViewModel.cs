using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Settings;
using Nagi.Presentation.Shell;

namespace Nagi.Presentation.Lists;

public partial class PlaylistViewModelItem : ObservableObject
{
    public PlaylistViewModelItem(Playlist playlist)
    {
        Id = playlist.Id;
        Name = playlist.Name;
        CoverImageUri = ImageUriHelper.GetUriWithCacheBuster(playlist.CoverImageUri);
        IsSmart = false;
        DateCreated = playlist.DateCreated;
        DateModified = playlist.DateModified;
        SongCount = playlist.PlaylistSongs.Count;
        IsFromServer = playlist.IsFromServer;
        ServerName = playlist.ServerFolder?.Name;
    }

    public PlaylistViewModelItem(SmartPlaylist smartPlaylist, int matchingSongCount = 0)
    {
        Id = smartPlaylist.Id;
        Name = smartPlaylist.Name;
        CoverImageUri = ImageUriHelper.GetUriWithCacheBuster(smartPlaylist.CoverImageUri);
        IsSmart = true;
        DateCreated = smartPlaylist.DateCreated;
        DateModified = smartPlaylist.DateModified;

        SongCount = matchingSongCount;
    }

    public Guid Id { get; }

    public bool IsSmart { get; }

    /// <summary>
    ///     The name of the music server the playlist comes from, or null for a playlist made in Nagi.
    /// </summary>
    public string? ServerName { get; }

    public bool IsFromServer { get; }

    public DateTime DateCreated { get; }

    public DateTime DateModified { get; set; }

    [ObservableProperty] public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    [NotifyPropertyChangedFor(nameof(IsCustomImage))]
    public partial string? CoverImageUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverImageUri);
    public bool IsCustomImage => ImageStorageHelper.IsCustomImage(CoverImageUri);

    public int SongCount { get; }

    public string Subtitle
    {
        get
        {
            var count = ResourceFormatter.Format((IsSmart, SongCount == 1) switch
            {
                (true, true) => Strings.Playlist_Smart_Count_Singular,
                (true, false) => Strings.Playlist_Smart_Count_Plural,
                (false, true) => Strings.Playlist_Count_Singular,
                _ => Strings.Playlist_Count_Plural
            }, SongCount);
            return ServerName is null ? count : ResourceFormatter.Format(Strings.Playlist_FromServer_Format, ServerName, count);
        }
    }

    public override string ToString() => Name;
}

public partial class PlaylistViewModel : ClientFilteredListViewModelBase<PlaylistViewModelItem>
{
    private static readonly PlaylistSortOrder[] SortOrders =
    [
        PlaylistSortOrder.NameAsc, PlaylistSortOrder.NameDesc, PlaylistSortOrder.DateCreatedAsc,
        PlaylistSortOrder.DateCreatedDesc, PlaylistSortOrder.DateModifiedAsc, PlaylistSortOrder.DateModifiedDesc
    ];

    private readonly ILibraryReader _libraryReader;
    private readonly IPlaylistService _playlistService;
    private readonly ISmartPlaylistService _smartPlaylistService;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly IMusicNavigationService _navigationService;
    private readonly IUIService _uiService;
    private int _busyCount;

    public PlaylistViewModel(ILibraryReader libraryReader, ILibraryNotifier libraryNotifier, IPlaylistService playlistService,
        ISmartPlaylistService smartPlaylistService, IMusicPlaybackService musicPlaybackService,
        IMusicNavigationService navigationService, IUIService uiService, ISettingsService settingsService,
        IDispatcherService dispatcherService, ILogger<PlaylistViewModel> logger)
        : base(libraryNotifier, dispatcherService, logger)
    {
        _libraryReader = libraryReader;
        _playlistService = playlistService;
        _smartPlaylistService = smartPlaylistService;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _uiService = uiService;
        Sort = new SortOrderSelection<PlaylistSortOrder>(SortOrders, AppSettings.PlaylistsSortOrder, settingsService, () =>
        {
            ApplyFilter();
            return Task.CompletedTask;
        }, logger);

        _playlistService.PlaylistUpdated += OnPlaylistUpdated;
        _smartPlaylistService.PlaylistUpdated += OnPlaylistUpdated;
        _playlistService.PlaylistsChanged += OnPlaylistsChanged;
        _smartPlaylistService.PlaylistsChanged += OnPlaylistsChanged;
    }

    public SortOrderSelection<PlaylistSortOrder> Sort { get; }

    /// <summary>Whether the list is loading or a playlist is being changed, so other changes must wait.</summary>
    public bool IsAnyOperationInProgress => IsLoading || _busyCount > 0;

    protected override void OnLoadingChanged() => OnPropertyChanged(nameof(IsAnyOperationInProgress));

    // Playlists follow their own change events, not the library's.
    protected override bool ShouldReloadOnLibraryChange(LibraryChange change) => false;

    protected override async Task<IEnumerable<PlaylistViewModelItem>> LoadItemsAsync(CancellationToken token)
    {
        var playlistsTask = _libraryReader.GetAllPlaylistsAsync();
        var smartPlaylistsTask = _smartPlaylistService.GetAllSmartPlaylistsAsync();
        var matchCountsTask = _smartPlaylistService.GetAllMatchingSongCountsAsync();
        await Task.WhenAll(playlistsTask, smartPlaylistsTask, matchCountsTask);

        var matchCounts = matchCountsTask.Result;
        return playlistsTask.Result.Select(playlist => new PlaylistViewModelItem(playlist))
            .Concat(smartPlaylistsTask.Result.Select(playlist =>
                new PlaylistViewModelItem(playlist, matchCounts.GetValueOrDefault(playlist.Id))))
            .ToList();
    }

    protected override bool MatchesSearch(PlaylistViewModelItem playlist, string searchTerm) =>
        playlist.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);

    protected override IEnumerable<PlaylistViewModelItem> Arrange(IEnumerable<PlaylistViewModelItem> playlists) => Sort.Current switch
    {
        PlaylistSortOrder.NameDesc => playlists.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
        PlaylistSortOrder.DateCreatedDesc => playlists.OrderByDescending(p => p.DateCreated).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
        PlaylistSortOrder.DateCreatedAsc => playlists.OrderBy(p => p.DateCreated).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
        PlaylistSortOrder.DateModifiedDesc => playlists.OrderByDescending(p => p.DateModified).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
        PlaylistSortOrder.DateModifiedAsc => playlists.OrderBy(p => p.DateModified).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
        _ => playlists.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)
    };

    public void OpenPlaylist(PlaylistViewModelItem playlist)
    {
        if (playlist.IsSmart)
            _navigationService.OpenSmartPlaylist(playlist.Id, playlist.Name, playlist.CoverImageUri);
        else
            _navigationService.OpenPlaylist(playlist.Id, playlist.Name, playlist.CoverImageUri);
    }

    [RelayCommand]
    private async Task PlayPlaylistAsync(PlaylistViewModelItem? playlist)
    {
        if (playlist is null || IsAnyOperationInProgress || playlist.Id == Guid.Empty) return;
        var (playlistId, isSmart) = (playlist.Id, playlist.IsSmart);

        await RunAsync(async () =>
        {
            if (!isSmart)
                await _musicPlaybackService.PlayPlaylistAsync(playlistId);
            else if (!await _musicPlaybackService.PlaySmartPlaylistAsync(playlistId))
                await ShowMessageAsync(Strings.PlaylistPage_Title, Strings.Playlist_Status_SmartEmpty);
        }, Strings.Playlist_Status_ErrorPlayback);
    }

    /// <summary>
    ///     Picks a regular or smart playlist with odds in proportion to how many of each there are, and plays it.
    /// </summary>
    [RelayCommand]
    private async Task PlayRandomPlaylistAsync()
    {
        if (IsAnyOperationInProgress) return;

        await RunAsync(async () =>
        {
            var regularCountTask = _libraryReader.GetPlaylistCountAsync();
            var smartCountTask = _smartPlaylistService.GetSmartPlaylistCountAsync();
            await Task.WhenAll(regularCountTask, smartCountTask);
            var regularCount = regularCountTask.Result;
            var totalCount = regularCount + smartCountTask.Result;

            if (totalCount > 0 && Random.Shared.Next(totalCount) < regularCount)
            {
                if (await _libraryReader.GetRandomPlaylistIdAsync() is { } id)
                {
                    await _musicPlaybackService.PlayPlaylistAsync(id);
                    return;
                }
            }
            else if (totalCount > 0 && await _smartPlaylistService.GetRandomSmartPlaylistIdAsync() is { } smartId)
            {
                if (!await _musicPlaybackService.PlaySmartPlaylistAsync(smartId))
                    await ShowMessageAsync(Strings.PlaylistPage_Title, Strings.Playlist_Status_SmartSelectedEmpty);
                return;
            }

            await ShowMessageAsync(Strings.PlaylistPage_Title, Strings.Playlist_Status_NoPlaylists);
        }, Strings.Playlist_Status_ErrorRandom);
    }

    /// <summary>
    ///     Opens the smart playlist editor and reloads the list if a playlist was created.
    /// </summary>
    [RelayCommand]
    private async Task CreateSmartPlaylistAsync()
    {
        if (_isDisposed || IsAnyOperationInProgress) return;

        var created = await _uiService.ShowSmartPlaylistEditorAsync();
        if (created is not null && !_isDisposed) await LoadAsync(CancellationToken.None);
    }

    /// <summary>
    ///     Asks for a name and optional cover image, then creates the playlist.
    /// </summary>
    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        if (_isDisposed || IsAnyOperationInProgress) return;

        var request = await _uiService.ShowCreatePlaylistDialogAsync();
        if (request is null || _isDisposed) return;

        var (playlistName, coverImageUri) = (request.Name, request.CoverImagePath);
        if (string.IsNullOrWhiteSpace(playlistName) || IsAnyOperationInProgress) return;

        await MutateAsync(async () =>
        {
            var normalizedName = TextNormalizer.Normalize(playlistName) ?? playlistName;
            try
            {
                var newPlaylist = await _playlistService.CreatePlaylistAsync(normalizedName, coverImageUri: coverImageUri);
                AllItems.Add(new PlaylistViewModelItem(newPlaylist));
                ApplyFilter();
            }
            catch (PlaylistNameTakenException)
            {
                await ShowMessageAsync(Strings.Generic_Error, Strings.SmartPlaylist_Error_DuplicateName);
            }
        }, Strings.Playlist_Status_CreateError);
    }

    /// <summary>
    ///     Asks for an image file and makes it the cover of <paramref name="playlist" />.
    /// </summary>
    [RelayCommand]
    private async Task ChangePlaylistCoverAsync(PlaylistViewModelItem? playlist)
    {
        if (playlist is null || IsAnyOperationInProgress) return;

        var newCoverImageUri = await _uiService.PickSingleFileAsync(FileExtensions.ImageFileExtensions);
        if (string.IsNullOrWhiteSpace(newCoverImageUri)) return;

        await SetPlaylistCoverAsync(playlist.Id, playlist.IsSmart, newCoverImageUri);
    }

    [RelayCommand]
    private Task RemovePlaylistCoverAsync(PlaylistViewModelItem? playlist) =>
        playlist is null ? Task.CompletedTask : SetPlaylistCoverAsync(playlist.Id, playlist.IsSmart, null);

    private async Task SetPlaylistCoverAsync(Guid playlistId, bool isSmart, string? coverImageUri)
    {
        if (IsAnyOperationInProgress || playlistId == Guid.Empty) return;

        // PlaylistUpdated supplies the persisted cache path, not the selected source image.
        await MutateAsync(async () =>
        {
            var success = isSmart
                ? await _smartPlaylistService.UpdateSmartPlaylistCoverAsync(playlistId, coverImageUri)
                : await _playlistService.UpdatePlaylistCoverAsync(playlistId, coverImageUri);
            if (!success)
                await ShowMessageAsync(Strings.Generic_Error, coverImageUri is null
                    ? Strings.Playlist_Status_RemoveCoverFailed
                    : Strings.Playlist_Status_UpdateCoverFailed);
        }, coverImageUri is null ? Strings.Playlist_Status_RemoveCoverError : Strings.Playlist_Status_UpdateCoverError);
    }

    /// <summary>
    ///     Asks for a new name and renames <paramref name="playlist" /> (regular or smart).
    /// </summary>
    [RelayCommand]
    private async Task RenamePlaylistAsync(PlaylistViewModelItem? playlist)
    {
        if (playlist is null || IsAnyOperationInProgress) return;

        var newName = await _uiService.ShowRenamePlaylistDialogAsync(playlist.Name);
        var (playlistId, isSmart) = (playlist.Id, playlist.IsSmart);
        if (string.IsNullOrWhiteSpace(newName) || _isDisposed || IsAnyOperationInProgress) return;

        await MutateAsync(async () =>
        {
            var normalizedName = TextNormalizer.Normalize(newName) ?? newName;
            try
            {
                var success = isSmart
                    ? await _smartPlaylistService.RenameSmartPlaylistAsync(playlistId, normalizedName)
                    : await _playlistService.RenamePlaylistAsync(playlistId, normalizedName);
                if (!success)
                {
                    await ShowMessageAsync(Strings.Generic_Error, Strings.Playlist_Status_RenameFailed);
                    return;
                }

                if (AllItems.FirstOrDefault(p => p.Id == playlistId) is { } item) item.Name = normalizedName;
                ApplyFilter();
            }
            catch (PlaylistNameTakenException)
            {
                await ShowMessageAsync(Strings.Generic_Error, Strings.SmartPlaylist_Error_DuplicateName);
            }
        }, Strings.Playlist_Status_RenameError);
    }

    /// <summary>
    ///     Asks for confirmation and deletes <paramref name="playlist" /> (regular or smart).
    /// </summary>
    [RelayCommand]
    private async Task DeletePlaylistAsync(PlaylistViewModelItem? playlist)
    {
        if (playlist is null || IsAnyOperationInProgress) return;

        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Strings.PlaylistPage_DeleteDialog_Title,
            ResourceFormatter.Format(Strings.PlaylistPage_DeleteDialog_Content_Format, playlist.Name),
            Strings.PlaylistPage_DeleteDialog_DeleteButton,
            Strings.Generic_Cancel,
            defaultToCancel: true);
        var (playlistId, isSmart) = (playlist.Id, playlist.IsSmart);
        if (!confirmed || _isDisposed || IsAnyOperationInProgress) return;

        await MutateAsync(async () =>
        {
            var success = isSmart
                ? await _smartPlaylistService.DeleteSmartPlaylistAsync(playlistId)
                : await _playlistService.DeletePlaylistAsync(playlistId);
            if (!success)
            {
                await ShowMessageAsync(Strings.Generic_Error, Strings.Playlist_Status_DeleteFailed);
                return;
            }

            AllItems.RemoveAll(p => p.Id == playlistId);
            ApplyFilter();
        }, Strings.Playlist_Status_DeleteError);
    }

    private Task RunAsync(Func<Task> action, string failureMessage) =>
        UserAction.RunAsync(action, _logger, _uiService, () => _isDisposed, failureMessage);

    // The playlist services report this view model's own changes too; they are already applied, so no reload follows.
    private async Task MutateAsync(Func<Task> action, string failureMessage)
    {
        _busyCount++;
        OnPropertyChanged(nameof(IsAnyOperationInProgress));
        try
        {
            await RunAsync(action, failureMessage);
        }
        finally
        {
            _busyCount--;
            OnPropertyChanged(nameof(IsAnyOperationInProgress));
        }
    }

    private Task ShowMessageAsync(string title, string message) =>
        _isDisposed ? Task.CompletedTask : _uiService.ShowMessageDialogAsync(title, message);

    public override void Dispose()
    {
        _playlistService.PlaylistUpdated -= OnPlaylistUpdated;
        _smartPlaylistService.PlaylistUpdated -= OnPlaylistUpdated;
        _playlistService.PlaylistsChanged -= OnPlaylistsChanged;
        _smartPlaylistService.PlaylistsChanged -= OnPlaylistsChanged;
        base.Dispose();
    }

    private void OnPlaylistUpdated(object? sender, PlaylistUpdatedEventArgs e)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            // Visible entries share these instances. Also update hidden entries so changing
            // the search filter cannot bring back stale artwork or modification dates.
            if (AllItems.FirstOrDefault(p => p.Id == e.PlaylistId) is not { } item) return;
            item.CoverImageUri = ImageUriHelper.GetUriWithCacheBuster(e.CoverImageUri, e.DateModified);
            item.DateModified = e.DateModified;
            if (Sort.Current is PlaylistSortOrder.DateModifiedAsc or PlaylistSortOrder.DateModifiedDesc)
                ApplyFilter();
        });
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        if (_busyCount > 0) return;

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed || _busyCount > 0) return;
            _logger.LogDebug("External playlist change detected. Reloading playlists...");
            _ = LoadAsync(CancellationToken.None);
        });
    }
}
