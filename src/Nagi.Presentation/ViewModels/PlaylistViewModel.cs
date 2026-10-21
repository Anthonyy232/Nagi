using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

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

    public DateTime DateCreated { get; }

    public DateTime DateModified { get; set; }



    [ObservableProperty] public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    [NotifyPropertyChangedFor(nameof(IsCustomImage))]
    public partial string? CoverImageUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverImageUri);
    public bool IsCustomImage => CoverImageUri?.Contains(".custom.") == true;

    public int SongCount { get; }

    public string SongCountText => ResourceFormatter.Format((IsSmart, SongCount == 1) switch
    {
        (true, true) => Strings.Playlist_Smart_Count_Singular,
        (true, false) => Strings.Playlist_Smart_Count_Plural,
        (false, true) => Strings.Playlist_Count_Singular,
        _ => Strings.Playlist_Count_Plural
    }, SongCount);

    public override string ToString() => Name;
}

public partial class PlaylistViewModel : SearchableViewModelBase, IDisposable
{
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly ILibraryReader _libraryReader;
    private readonly IPlaylistService _playlistService;
    private readonly ISmartPlaylistService _smartPlaylistService;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly IUISettingsService _settingsService;
    private readonly IUIService _uiService;
    private bool _hasSortOrderLoaded;
    private readonly List<PlaylistViewModelItem> _allPlaylists = new();
    private bool _isNavigating;
    private int _isMutating;
    private int _loadVersion;
    private bool _isDisposed;

    public PlaylistViewModel(ILibraryReader libraryReader, IPlaylistService playlistService, ISmartPlaylistService smartPlaylistService,
        IMusicPlaybackService musicPlaybackService, INavigationService navigationService, IUIService uiService,
        IUISettingsService settingsService, IDispatcherService dispatcherService, ILogger<PlaylistViewModel> logger)
        : base(dispatcherService, logger)
    {
        _libraryReader = libraryReader;
        _playlistService = playlistService;
        _smartPlaylistService = smartPlaylistService;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _uiService = uiService;
        _settingsService = settingsService;

        _collectionChangedHandler = (s, e) => OnPropertyChanged(nameof(HasPlaylists));
        Playlists.CollectionChanged += _collectionChangedHandler;

        _playlistService.PlaylistUpdated += OnPlaylistUpdated;
        _smartPlaylistService.PlaylistUpdated += OnPlaylistUpdated;

        _playlistService.PlaylistsChanged += OnPlaylistsChanged;
        _smartPlaylistService.PlaylistsChanged += OnPlaylistsChanged;

        UpdateSortOrderText();
    }

    private void UpdateSortOrderText()
    {
        CurrentSortOrderText = SortOrderHelper.GetDisplayName(CurrentSortOrder);
    }

    public ObservableRangeCollection<PlaylistViewModelItem> Playlists { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsCreatingPlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsRenamingPlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsDeletingPlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsUpdatingCover { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsLoadingPlaylists { get; set; }

    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty] public partial PlaylistSortOrder CurrentSortOrder { get; set; } = PlaylistSortOrder.NameAsc;

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = string.Empty;

    partial void OnCurrentSortOrderChanged(PlaylistSortOrder value) => UpdateSortOrderText();


    public bool IsAnyOperationInProgress =>
        IsLoadingPlaylists || IsCreatingPlaylist || IsRenamingPlaylist || IsDeletingPlaylist || IsUpdatingCover;

    public bool HasPlaylists => Playlists.Count > 0;


    public void NavigateToPlaylistDetail(PlaylistViewModelItem? playlist)
    {
        if (playlist is null || _isNavigating) return;

        _isNavigating = true;
        try
        {
            if (playlist.IsSmart)
            {
                var navParam = new SmartPlaylistSongViewNavigationParameter
                {
                    Title = playlist.Name,
                    SmartPlaylistId = playlist.Id,
                    CoverImageUri = playlist.CoverImageUri
                };
                _navigationService.NavigateTo(navParam);
            }
            else
            {
                var navParam = new PlaylistSongViewNavigationParameter
                {
                    Title = playlist.Name,
                    PlaylistId = playlist.Id,
                    CoverImageUri = playlist.CoverImageUri
                };
                _navigationService.NavigateTo(navParam);
            }
        }
        finally
        {
            _isNavigating = false;
        }
    }

    /// <summary>
    ///     Clears the current queue and starts playing the selected playlist (regular or smart).
    /// </summary>
    [RelayCommand]
    private async Task PlayPlaylistAsync(PlaylistViewModelItem? playlist)
    {
        if (playlist is null || IsAnyOperationInProgress || playlist.Id == Guid.Empty) return;
        var (playlistId, isSmart) = (playlist.Id, playlist.IsSmart);

        StatusMessage = Strings.Playlist_Status_Starting;
        try
        {
            if (isSmart)
            {
                var songIds = await _smartPlaylistService.GetMatchingSongIdsAsync(playlistId);
                if (songIds.Count == 0)
                {
                    StatusMessage = Strings.Playlist_Status_SmartEmpty;
                    _logger.LogDebug("Smart playlist {PlaylistId} has no matching songs", playlistId);
                    return;
                }
                await _musicPlaybackService.PlayAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.SmartPlaylist, playlistId));
            }
            else
            {
                await _musicPlaybackService.PlayPlaylistAsync(playlistId);
            }
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Playlist_Status_ErrorPlayback;
            _logger.LogCritical(ex, "Error playing playlist {PlaylistId}", playlistId);
        }
    }

    /// <summary>
    ///     Selects a random playlist (Regular or Smart) based on the total counts of each type and starts playback.
    /// </summary>
    [RelayCommand]
    private async Task PlayRandomPlaylistAsync()
    {
        if (IsAnyOperationInProgress) return;
        StatusMessage = Strings.Playlist_Status_PickingRandom;

        try
        {
            var regularCountTask = _libraryReader.GetPlaylistCountAsync();
            var smartCountTask = _smartPlaylistService.GetSmartPlaylistCountAsync();
            await Task.WhenAll(regularCountTask, smartCountTask);
            var regularCount = await regularCountTask;
            var smartCount = await smartCountTask;

            var totalCount = regularCount + smartCount;
            if (totalCount == 0)
            {
                StatusMessage = Strings.Playlist_Status_NoPlaylists;
                return;
            }

            var randomTicket = Random.Shared.Next(totalCount);

            if (randomTicket < regularCount)
            {
                var id = await _libraryReader.GetRandomPlaylistIdAsync();
                if (id.HasValue)
                {
                    await _musicPlaybackService.PlayPlaylistAsync(id.Value);
                    StatusMessage = string.Empty;
                }
                else StatusMessage = Strings.Playlist_Status_NoPlaylists;
            }
            else
            {
                var id = await _smartPlaylistService.GetRandomSmartPlaylistIdAsync();
                if (id.HasValue)
                {
                    var songIds = await _smartPlaylistService.GetMatchingSongIdsAsync(id.Value);
                    if (songIds.Count > 0)
                    {
                        await _musicPlaybackService.PlayAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.SmartPlaylist, id.Value));
                        StatusMessage = string.Empty;
                    }
                    else
                    {
                        StatusMessage = Strings.Playlist_Status_SmartSelectedEmpty;
                    }
                }
                else StatusMessage = Strings.Playlist_Status_NoPlaylists;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Playlist_Status_ErrorRandom;
            _logger.LogCritical(ex, "Error playing random playlist");
        }
    }

    /// <summary>
    ///     Opens the smart playlist editor and reloads the list if a playlist was created.
    /// </summary>
    [RelayCommand]
    private async Task CreateSmartPlaylistAsync()
    {
        if (_isDisposed || IsAnyOperationInProgress) return;

        try
        {
            var created = await _uiService.ShowSmartPlaylistEditorAsync();
            if (created is not null && !_isDisposed) await LoadPlaylistsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create a smart playlist");
        }
    }

    [RelayCommand]
    private async Task LoadPlaylistsAsync()
    {
        if (_isDisposed || IsAnyOperationInProgress && !IsLoadingPlaylists) return;
        var loadVersion = ++_loadVersion;
        IsLoadingPlaylists = true;
        try
        {
            StatusMessage = Strings.Playlist_Status_Loading;
            Task<PlaylistSortOrder>? sortTask = null;
            if (!_hasSortOrderLoaded)
            {
                sortTask = _settingsService.GetSortOrderAsync<PlaylistSortOrder>(SortOrderHelper.PlaylistsSortOrderKey);
            }

            var playlistsTask = _libraryReader.GetAllPlaylistsAsync();
            var smartPlaylistsTask = _smartPlaylistService.GetAllSmartPlaylistsAsync();
            var matchCountsTask = _smartPlaylistService.GetAllMatchingSongCountsAsync();

            if (sortTask != null)
                await Task.WhenAll(sortTask, playlistsTask, smartPlaylistsTask, matchCountsTask);
            else
                await Task.WhenAll(playlistsTask, smartPlaylistsTask, matchCountsTask);

            if (_isDisposed || loadVersion != _loadVersion) return;

            if (sortTask != null)
            {
                CurrentSortOrder = sortTask.Result;
                _hasSortOrderLoaded = true;
            }

            _allPlaylists.Clear();

            _allPlaylists.AddRange(playlistsTask.Result.Select(playlist => new PlaylistViewModelItem(playlist)));
            var matchCounts = matchCountsTask.Result;
            _allPlaylists.AddRange(smartPlaylistsTask.Result.Select(playlist =>
                new PlaylistViewModelItem(playlist, matchCounts.GetValueOrDefault(playlist.Id))));
            ApplyFilter();

            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            if (_isDisposed || loadVersion != _loadVersion) return;
            StatusMessage = Strings.Playlist_Status_ErrorLoadingPlaylists;
            _logger.LogError(ex, "Error loading playlists");
        }
        finally
        {
            if (loadVersion == _loadVersion) IsLoadingPlaylists = false;
        }
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

        IsCreatingPlaylist = true;
        Interlocked.Increment(ref _isMutating);
        StatusMessage = Strings.Playlist_Status_Creating;

        try
        {
            var normalizedName = ArtistNameHelper.NormalizeStringCore(playlistName) ?? playlistName;
            var newPlaylist = await _playlistService.CreatePlaylistAsync(normalizedName, coverImageUri: coverImageUri);
            if (newPlaylist != null)
            {
                var newItem = new PlaylistViewModelItem(newPlaylist);
                _allPlaylists.Add(newItem);
                ApplyFilter();
                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = Strings.Playlist_Status_CreateFailed;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Playlist_Status_CreateError;
            _logger.LogError(ex, "Error creating playlist");
        }
        finally
        {
            Interlocked.Decrement(ref _isMutating);
            IsCreatingPlaylist = false;
        }
    }

    /// <summary>
    ///     Asks for an image file and makes it the cover of <paramref name="playlist" />.
    /// </summary>
    [RelayCommand]
    private async Task ChangePlaylistCoverAsync(PlaylistViewModelItem? playlist)
    {
        if (playlist is null || IsAnyOperationInProgress) return;

        var newCoverImageUri = await _uiService.PickSingleFileAsync(FileExtensions.ImageFileExtensions);
        if (string.IsNullOrWhiteSpace(newCoverImageUri))
        {
            _logger.LogDebug("User cancelled cover image selection.");
            return;
        }

        await SetPlaylistCoverAsync(playlist.Id, playlist.IsSmart, newCoverImageUri);
    }

    [RelayCommand]
    private Task RemovePlaylistCoverAsync(PlaylistViewModelItem? playlist) =>
        playlist is null ? Task.CompletedTask : SetPlaylistCoverAsync(playlist.Id, playlist.IsSmart, null);

    private async Task SetPlaylistCoverAsync(Guid playlistId, bool isSmart, string? coverImageUri)
    {
        if (IsAnyOperationInProgress || playlistId == Guid.Empty) return;

        IsUpdatingCover = true;
        Interlocked.Increment(ref _isMutating);
        StatusMessage = coverImageUri is null
            ? Strings.Playlist_Status_RemovingCover
            : Strings.Playlist_Status_UpdatingCover;

        try
        {
            var success = isSmart
                ? await _smartPlaylistService.UpdateSmartPlaylistCoverAsync(playlistId, coverImageUri)
                : await _playlistService.UpdatePlaylistCoverAsync(playlistId, coverImageUri);

            // PlaylistUpdated supplies the persisted cache path, not the selected source image.
            StatusMessage = success ? string.Empty : coverImageUri is null
                ? Strings.Playlist_Status_RemoveCoverFailed
                : Strings.Playlist_Status_UpdateCoverFailed;
        }
        catch (Exception ex)
        {
            StatusMessage = coverImageUri is null
                ? Strings.Playlist_Status_RemoveCoverError
                : Strings.Playlist_Status_UpdateCoverError;
            _logger.LogError(ex, "Error updating playlist cover for {PlaylistId}", playlistId);
        }
        finally
        {
            Interlocked.Decrement(ref _isMutating);
            IsUpdatingCover = false;
        }
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

        IsRenamingPlaylist = true;
        Interlocked.Increment(ref _isMutating);
        StatusMessage = Strings.Playlist_Status_Renaming;

        try
        {
            bool success;
            var normalizedName = ArtistNameHelper.NormalizeStringCore(newName) ?? newName;

            if (isSmart)
                success = await _smartPlaylistService.RenameSmartPlaylistAsync(playlistId, normalizedName);
            else
                success = await _playlistService.RenamePlaylistAsync(playlistId, normalizedName);

            if (success)
            {
                var backingItem = _allPlaylists.FirstOrDefault(p => p.Id == playlistId);
                if (backingItem != null) backingItem.Name = normalizedName;

                ApplyFilter();

                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = Strings.Playlist_Status_RenameFailed;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Playlist_Status_RenameError;
            _logger.LogError(ex, "Error renaming playlist {PlaylistId}", playlistId);
        }
        finally
        {
            Interlocked.Decrement(ref _isMutating);
            IsRenamingPlaylist = false;
        }
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
            string.Format(Strings.PlaylistPage_DeleteDialog_Content_Format, playlist.Name),
            Strings.PlaylistPage_DeleteDialog_DeleteButton,
            Strings.PlaylistPage_CreateDialog_CancelButton,
            defaultToCancel: true);
        var (playlistId, isSmart) = (playlist.Id, playlist.IsSmart);
        if (!confirmed || _isDisposed || IsAnyOperationInProgress) return;

        IsDeletingPlaylist = true;
        Interlocked.Increment(ref _isMutating);
        StatusMessage = Strings.Playlist_Status_Deleting;

        try
        {
            bool success;
            if (isSmart)
                success = await _smartPlaylistService.DeleteSmartPlaylistAsync(playlistId);
            else
                success = await _playlistService.DeletePlaylistAsync(playlistId);

            if (success)
            {
                _allPlaylists.RemoveAll(p => p.Id == playlistId);
                ApplyFilter();
                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = Strings.Playlist_Status_DeleteFailed;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Playlist_Status_DeleteError;
            _logger.LogError(ex, "Error deleting playlist {PlaylistId}", playlistId);
        }
        finally
        {
            Interlocked.Decrement(ref _isMutating);
            IsDeletingPlaylist = false;
        }
    }

    protected override Task ExecuteSearchAsync(CancellationToken token) =>
        _dispatcherService.EnqueueAsync(() =>
        {
            if (!token.IsCancellationRequested && !_isDisposed) ApplyFilter();
            return Task.CompletedTask;
        });

    private void ApplyFilter()
    {
        IEnumerable<PlaylistViewModelItem> filtered = _allPlaylists;
        if (IsSearchActive)
            filtered = _allPlaylists.Where(p =>
                p.Name.Contains(SearchTerm, StringComparison.OrdinalIgnoreCase));

        var sorted = CurrentSortOrder switch
        {
            PlaylistSortOrder.NameDesc => filtered.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            PlaylistSortOrder.DateCreatedDesc => filtered.OrderByDescending(p => p.DateCreated).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            PlaylistSortOrder.DateCreatedAsc => filtered.OrderBy(p => p.DateCreated).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            PlaylistSortOrder.DateModifiedDesc => filtered.OrderByDescending(p => p.DateModified).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            PlaylistSortOrder.DateModifiedAsc => filtered.OrderBy(p => p.DateModified).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            _ => filtered.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)
        };

        Playlists.ReplaceRange(sorted);

    }

    [RelayCommand]
    public async Task ChangeSortOrderAsync(string sortOrderString)
    {
        if (Enum.TryParse<PlaylistSortOrder>(sortOrderString, out var newSortOrder)
            && newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            try { await _settingsService.SetSortOrderAsync(SortOrderHelper.PlaylistsSortOrderKey, newSortOrder); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to save playlist sort order"); }
            ApplyFilter();
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        CancelPendingSearch();
        Playlists.CollectionChanged -= _collectionChangedHandler;
        _playlistService.PlaylistUpdated -= OnPlaylistUpdated;
        _smartPlaylistService.PlaylistUpdated -= OnPlaylistUpdated;
        _playlistService.PlaylistsChanged -= OnPlaylistsChanged;
        _smartPlaylistService.PlaylistsChanged -= OnPlaylistsChanged;
    }

    private void OnPlaylistUpdated(object? sender, PlaylistUpdatedEventArgs e)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            // Visible entries share these instances. Also update hidden entries so changing
            // the search filter cannot bring back stale artwork or modification dates.
            var backingItem = _allPlaylists.FirstOrDefault(p => p.Id == e.PlaylistId);
            if (backingItem != null)
            {
                backingItem.CoverImageUri = null;
                backingItem.CoverImageUri = e.DateModified.HasValue
                    ? ImageUriHelper.GetUriWithCacheBuster(e.CoverImageUri, e.DateModified)
                    : ImageUriHelper.GetUriWithCacheBuster(e.CoverImageUri);
                if (e.DateModified.HasValue)
                {
                    backingItem.DateModified = e.DateModified.Value;
                    if (CurrentSortOrder is PlaylistSortOrder.DateModifiedAsc or PlaylistSortOrder.DateModifiedDesc)
                        ApplyFilter();
                }
            }
        });
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        // Ignore synchronous service events from our own mutations.
        if (_isMutating > 0) return;

        _dispatcherService.TryEnqueue(() =>
        {
            if (!_isDisposed && (!IsAnyOperationInProgress || IsLoadingPlaylists))
            {
                _logger.LogDebug("External playlist change detected. Reloading playlists...");
                _ = LoadPlaylistsCommand.ExecuteAsync(null);
            }
        });
    }
}
