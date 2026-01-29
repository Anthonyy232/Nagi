using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
using Nagi.Core.Helpers;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Represents a single playlist item for display in the UI.
///     Supports both regular playlists and smart playlists.
/// </summary>
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
        UpdateSongCount(playlist.PlaylistSongs?.Count ?? 0);
    }

    public PlaylistViewModelItem(SmartPlaylist smartPlaylist, int matchingSongCount = 0)
    {
        Id = smartPlaylist.Id;
        Name = smartPlaylist.Name;
        CoverImageUri = ImageUriHelper.GetUriWithCacheBuster(smartPlaylist.CoverImageUri);
        IsSmart = true;
        DateCreated = smartPlaylist.DateCreated;
        DateModified = smartPlaylist.DateModified;

        UpdateSongCount(matchingSongCount);
    }

    public Guid Id { get; }

    /// <summary>
    ///     Indicates whether this is a smart playlist (true) or regular playlist (false).
    /// </summary>
    public bool IsSmart { get; }

    public DateTime DateCreated { get; }

    public DateTime DateModified { get; }



    [ObservableProperty] public partial string Name { get; set; }

    [ObservableProperty] public partial string? CoverImageUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverImageUri);
    public bool IsCustomImage => CoverImageUri?.Contains(".custom.") == true;

    [ObservableProperty] public partial int SongCount { get; set; }

    [ObservableProperty] public partial string SongCountText { get; set; } = string.Empty;

    partial void OnCoverImageUriChanged(string? value)
    {
        OnPropertyChanged(nameof(IsArtworkAvailable));
        OnPropertyChanged(nameof(IsCustomImage));
    }

    /// <summary>
    ///     Updates the song count and the display text for this playlist item.
    /// </summary>
    public void UpdateSongCount(int newSongCount)
    {
        if (SongCount == newSongCount) return;

        SongCount = newSongCount;
        if (IsSmart)
        {
            SongCountText = newSongCount == 1 
                ? string.Format(Nagi.WinUI.Resources.Strings.Playlist_Smart_Count_Singular, newSongCount) 
                : string.Format(Nagi.WinUI.Resources.Strings.Playlist_Smart_Count_Plural, newSongCount);
        }
        else
        {
            SongCountText = newSongCount == 1 
                ? string.Format(Nagi.WinUI.Resources.Strings.Playlist_Count_Singular, newSongCount) 
                : string.Format(Nagi.WinUI.Resources.Strings.Playlist_Count_Plural, newSongCount);
        }
    }
}

/// <summary>
///     ViewModel for managing the collection of playlists.
/// </summary>
public partial class PlaylistViewModel : SearchableViewModelBase
{
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly ILibraryService _libraryService;
    private readonly ISmartPlaylistService _smartPlaylistService;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly IUISettingsService _settingsService;
    private bool _hasSortOrderLoaded;
    private List<PlaylistViewModelItem> _allPlaylists = new();
    private bool _isNavigating;

    public PlaylistViewModel(ILibraryService libraryService, ISmartPlaylistService smartPlaylistService,
        IMusicPlaybackService musicPlaybackService, INavigationService navigationService,
        IUISettingsService settingsService, IDispatcherService dispatcherService, ILogger<PlaylistViewModel> logger)
        : base(dispatcherService, logger)
    {
        _libraryService = libraryService;
        _smartPlaylistService = smartPlaylistService;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _settingsService = settingsService;

        // Store the handler in a field so we can reliably unsubscribe from it later.
        _collectionChangedHandler = (s, e) => OnPropertyChanged(nameof(HasPlaylists));
        Playlists.CollectionChanged += _collectionChangedHandler;

        _libraryService.PlaylistUpdated += OnPlaylistUpdated;
        _smartPlaylistService.PlaylistUpdated += OnPlaylistUpdated;
        
        UpdateSortOrderText();
    }

    private void UpdateSortOrderText()
    {
        CurrentSortOrderText = SortOrderHelper.GetDisplayName(CurrentSortOrder);
    }

    [ObservableProperty] public partial ObservableCollection<PlaylistViewModelItem> Playlists { get; set; } = new();

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

    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty] public partial PlaylistSortOrder CurrentSortOrder { get; set; } = PlaylistSortOrder.NameAsc;

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = string.Empty;

    partial void OnCurrentSortOrderChanged(PlaylistSortOrder value) => UpdateSortOrderText();


    /// <summary>
    ///     Gets a value indicating whether any background operation is in progress.
    /// </summary>
    public bool IsAnyOperationInProgress =>
        IsCreatingPlaylist || IsRenamingPlaylist || IsDeletingPlaylist || IsUpdatingCover;

    /// <summary>
    ///     Gets a value indicating whether there are any playlists to display.
    /// </summary>
    public bool HasPlaylists => Playlists.Any();


    /// <summary>
    ///     Navigates to the song list for the selected playlist.
    /// </summary>
    [RelayCommand]
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
                _navigationService.Navigate(typeof(SmartPlaylistSongViewPage), navParam);
            }
            else
            {
                var navParam = new PlaylistSongViewNavigationParameter
                {
                    Title = playlist.Name,
                    PlaylistId = playlist.Id,
                    CoverImageUri = playlist.CoverImageUri
                };
                _navigationService.Navigate(typeof(PlaylistSongViewPage), navParam);
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
    private async Task PlayPlaylistAsync(Tuple<Guid, bool> args)
    {
        var (playlistId, isSmart) = args;
        if (IsAnyOperationInProgress || playlistId == Guid.Empty) return;

        StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_Starting;
        try
        {
            if (isSmart)
            {
                var songIds = await _smartPlaylistService.GetMatchingSongIdsAsync(playlistId);
                if (songIds.Count == 0)
                {
                    StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_SmartEmpty;
                    _logger.LogDebug("Smart playlist {PlaylistId} has no matching songs", playlistId);
                    return;
                }
                await _musicPlaybackService.PlayAsync(songIds);
            }
            else
            {
                await _musicPlaybackService.PlayPlaylistAsync(playlistId);
            }
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_ErrorPlayback;
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
        StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_PickingRandom;

        try
        {
            // 1. Get current counts directly from services (most accurate)
            // Even though we load them into memory, fetching count is cheap and ensures we don't pick from stale UI state if not refreshed
            // However, to keep it fast and consistent with what the user sees, we can try to use standard service calls.
            
            // Actually, we need to know the RATIO to pick fairly.
            // Getting counts is fast.
            var regularCountTask = _libraryService.GetPlaylistCountAsync();
            var smartCountTask = _smartPlaylistService.GetSmartPlaylistCountAsync();
            
            var regularCount = await regularCountTask;
            var smartCount = await smartCountTask;
            
            var totalCount = regularCount + smartCount;
            if (totalCount == 0)
            {
                StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_NoPlaylists;
                return;
            }

            // 2. Weighted Random Selection
            var random = Random.Shared;
            var randomTicket = random.Next(totalCount); // 0 to totalCount - 1

            if (randomTicket < regularCount)
            {
                // Pick a Regular Playlist
                var id = await _libraryService.GetRandomPlaylistIdAsync();
                if (id.HasValue)
                {
                    await _musicPlaybackService.PlayPlaylistAsync(id.Value);
                    StatusMessage = string.Empty;
                }
            }
            else
            {
                // Pick a Smart Playlist
                var id = await _smartPlaylistService.GetRandomSmartPlaylistIdAsync();
                if (id.HasValue)
                {
                    // For smart playlists, we must resolve the songs first as MusicPlaybackService doesn't native support "PlaySmartPlaylist"
                    var songIds = await _smartPlaylistService.GetMatchingSongIdsAsync(id.Value);
                    if (songIds.Count > 0)
                    {
                        await _musicPlaybackService.PlayAsync(songIds);
                        StatusMessage = string.Empty;
                    }
                    else
                    {
                        StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_SmartSelectedEmpty;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_ErrorRandom;
            _logger.LogCritical(ex, "Error playing random playlist");
        }
    }

    /// <summary>
    ///     Loads all playlists (both regular and smart) from the services.
    /// </summary>
    [RelayCommand]
    private async Task LoadPlaylistsAsync()
    {
        try
        {
            StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_Loading;
            Task<PlaylistSortOrder>? sortTask = null;
            if (!_hasSortOrderLoaded)
            {
                sortTask = _settingsService.GetSortOrderAsync<PlaylistSortOrder>(SortOrderHelper.PlaylistsSortOrderKey);
            }

            var playlistsTask = _libraryService.GetAllPlaylistsAsync();
            var smartPlaylistsTask = _smartPlaylistService.GetAllSmartPlaylistsAsync();
            var matchCountsTask = _smartPlaylistService.GetAllMatchingSongCountsAsync();

            if (sortTask != null)
                await Task.WhenAll(sortTask, playlistsTask, smartPlaylistsTask, matchCountsTask).ConfigureAwait(false);
            else
                await Task.WhenAll(playlistsTask, smartPlaylistsTask, matchCountsTask).ConfigureAwait(false);

            if (sortTask != null)
            {
                CurrentSortOrder = sortTask.Result;
                _hasSortOrderLoaded = true;
            }

            Playlists.Clear();
            _allPlaylists.Clear();

            // Load regular playlists
            foreach (var playlist in playlistsTask.Result)
                _allPlaylists.Add(new PlaylistViewModelItem(playlist));

            // Load smart playlists with their match counts
            var smartPlaylistsFromDb = smartPlaylistsTask.Result;
            var matchCounts = matchCountsTask.Result;
            
            foreach (var smartPlaylist in smartPlaylistsFromDb)
            {
                var matchCount = matchCounts.GetValueOrDefault(smartPlaylist.Id, 0);
                _allPlaylists.Add(new PlaylistViewModelItem(smartPlaylist, matchCount));
            }

            // No need to sort here - ApplyFilter will handle sorting

            // Apply current filter and sort
            ApplyFilter();

            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_ErrorLoadingPlaylists;
            _logger.LogError(ex, "Error loading playlists");
        }
    }

    /// <summary>
    ///     Creates a new playlist.
    /// </summary>
    /// <param name="args">A tuple containing the playlist name and optional cover image URI.</param>
    [RelayCommand]
    private async Task CreatePlaylistAsync(Tuple<string, string?> args)
    {
        var (playlistName, coverImageUri) = args;
        if (string.IsNullOrWhiteSpace(playlistName) || IsAnyOperationInProgress) return;

        IsCreatingPlaylist = true;
        StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_Creating;

        try
        {
            var normalizedName = ArtistNameHelper.NormalizeStringCore(playlistName) ?? playlistName;
            var newPlaylist = await _libraryService.CreatePlaylistAsync(normalizedName, coverImageUri: coverImageUri);
            if (newPlaylist != null)
            {
                var newItem = new PlaylistViewModelItem(newPlaylist);
                Playlists.Add(newItem);
                _allPlaylists.Add(newItem);
                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_CreateFailed;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_CreateError;
            _logger.LogError(ex, "Error creating playlist");
        }
        finally
        {
            IsCreatingPlaylist = false;
        }
    }

    /// <summary>
    ///     Updates the cover image for an existing playlist (regular or smart).
    /// </summary>
    /// <param name="args">A tuple containing the playlist ID, the new cover image URI, and whether it's a smart playlist.</param>
    [RelayCommand]
    private async Task UpdatePlaylistCoverAsync(Tuple<Guid, string, bool> args)
    {
        var (playlistId, newCoverImageUri, isSmart) = args;
        if (string.IsNullOrWhiteSpace(newCoverImageUri) || IsAnyOperationInProgress) return;

        IsUpdatingCover = true;
        StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_UpdatingCover;

        try
        {
            bool success;
            if (isSmart)
                success = await _smartPlaylistService.UpdateSmartPlaylistCoverAsync(playlistId, newCoverImageUri);
            else
                success = await _libraryService.UpdatePlaylistCoverAsync(playlistId, newCoverImageUri);
            
            if (success)
            {
                var playlistItem = Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (playlistItem != null)
                {
                    // Force refresh by setting to null first, then apply cache-buster
                    playlistItem.CoverImageUri = null;
                    playlistItem.CoverImageUri = ImageUriHelper.GetUriWithCacheBuster(newCoverImageUri);
                }

                // Also update the backing list
                var backingItem = _allPlaylists.FirstOrDefault(p => p.Id == playlistId);
                if (backingItem != null)
                {
                    backingItem.CoverImageUri = ImageUriHelper.GetUriWithCacheBuster(newCoverImageUri);
                }

                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_UpdateCoverFailed;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_UpdateCoverError;
            _logger.LogError(ex, "Error updating playlist cover for {PlaylistId}", playlistId);
        }
        finally
        {
            IsUpdatingCover = false;
        }
    }

    [RelayCommand]
    private async Task RemovePlaylistCoverAsync(Tuple<Guid, bool> args)
    {
        var (playlistId, isSmart) = args;
        if (IsAnyOperationInProgress || playlistId == Guid.Empty) return;

        IsUpdatingCover = true;
        StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_RemovingCover;

        try
        {
            bool success;
            if (isSmart)
                success = await _smartPlaylistService.UpdateSmartPlaylistCoverAsync(playlistId, null);
            else
                success = await _libraryService.UpdatePlaylistCoverAsync(playlistId, null);
            
            if (success)
            {
                var playlistItem = Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (playlistItem != null)
                {
                    // Force refresh by setting to null first
                    playlistItem.CoverImageUri = null;
                }

                // Also update the backing list
                var backingItem = _allPlaylists.FirstOrDefault(p => p.Id == playlistId);
                if (backingItem != null)
                {
                    backingItem.CoverImageUri = null;
                }

                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_RemoveCoverFailed;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_RemoveCoverError;
            _logger.LogError(ex, "Error removing playlist cover for {PlaylistId}", playlistId);
        }
        finally
        {
            IsUpdatingCover = false;
        }
    }

    /// <summary>
    ///     Renames an existing playlist (regular or smart).
    /// </summary>
    /// <param name="args">A tuple containing the playlist ID, the new name, and whether it's a smart playlist.</param>
    [RelayCommand]
    private async Task RenamePlaylistAsync(Tuple<Guid, string, bool> args)
    {
        var (playlistId, newName, isSmart) = args;
        if (string.IsNullOrWhiteSpace(newName) || IsAnyOperationInProgress) return;

        IsRenamingPlaylist = true;
        StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_Renaming;

        try
        {
            bool success;
            var normalizedName = ArtistNameHelper.NormalizeStringCore(newName) ?? newName;

            if (isSmart)
                success = await _smartPlaylistService.RenameSmartPlaylistAsync(playlistId, normalizedName);
            else
                success = await _libraryService.RenamePlaylistAsync(playlistId, normalizedName);
            
            if (success)
            {
                var playlistItem = Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (playlistItem != null) playlistItem.Name = normalizedName;

                // Also update the backing list to keep filter/sort in sync
                var backingItem = _allPlaylists.FirstOrDefault(p => p.Id == playlistId);
                if (backingItem != null) backingItem.Name = normalizedName;

                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_RenameFailed;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_RenameError;
            _logger.LogError(ex, "Error renaming playlist {PlaylistId}", playlistId);
        }
        finally
        {
            IsRenamingPlaylist = false;
        }
    }

    /// <summary>
    ///     Deletes a playlist (regular or smart).
    /// </summary>
    /// <param name="args">A tuple containing the playlist ID, the new cover image URI, and whether it's a smart playlist.</param>
    [RelayCommand]
    private async Task DeletePlaylistAsync(Tuple<Guid, bool> args)
    {
        var (playlistId, isSmart) = args;
        if (IsAnyOperationInProgress) return;

        IsDeletingPlaylist = true;
        StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_Deleting;

        try
        {
            bool success;
            if (isSmart)
                success = await _smartPlaylistService.DeleteSmartPlaylistAsync(playlistId);
            else
                success = await _libraryService.DeletePlaylistAsync(playlistId);
            
            if (success)
            {
                var playlistItem = Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (playlistItem != null)
                {
                    Playlists.Remove(playlistItem);
                    _allPlaylists.RemoveAll(p => p.Id == playlistId);
                }
                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_DeleteFailed;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Nagi.WinUI.Resources.Strings.Playlist_Status_DeleteError;
            _logger.LogError(ex, "Error deleting playlist {PlaylistId}", playlistId);
        }
        finally
        {
            IsDeletingPlaylist = false;
        }
    }

    protected override async Task ExecuteSearchAsync(CancellationToken token)
    {
        await _dispatcherService.EnqueueAsync(async () =>
        {
            if (token.IsCancellationRequested) return;
            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        IEnumerable<PlaylistViewModelItem> filtered = _allPlaylists;
        if (IsSearchActive)
            filtered = _allPlaylists.Where(p =>
                p.Name?.Contains(SearchTerm, StringComparison.OrdinalIgnoreCase) == true);

        // Apply sort order
        var sorted = CurrentSortOrder switch
        {
            PlaylistSortOrder.NameDesc => filtered.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            PlaylistSortOrder.DateCreatedDesc => filtered.OrderByDescending(p => p.DateCreated).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            PlaylistSortOrder.DateCreatedAsc => filtered.OrderBy(p => p.DateCreated).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            PlaylistSortOrder.DateModifiedDesc => filtered.OrderByDescending(p => p.DateModified).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            PlaylistSortOrder.DateModifiedAsc => filtered.OrderBy(p => p.DateModified).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            _ => filtered.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)
        };

        // Replace collection in one operation to minimize CollectionChanged events
        // Must manage event subscription when swapping instances
        var newItems = sorted.ToList();
        
        Playlists.CollectionChanged -= _collectionChangedHandler;
        Playlists = new ObservableCollection<PlaylistViewModelItem>(newItems);
        Playlists.CollectionChanged += _collectionChangedHandler;
        
        OnPropertyChanged(nameof(HasPlaylists));
    }

    /// <summary>
    ///     Changes the sort order and reapplies filtering.
    /// </summary>
    [RelayCommand]
    public Task ChangeSortOrderAsync(string sortOrderString)
    {
        if (Enum.TryParse<PlaylistSortOrder>(sortOrderString, out var newSortOrder)
            && newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            _ = _settingsService.SetSortOrderAsync(SortOrderHelper.PlaylistsSortOrderKey, newSortOrder)
                .ContinueWith(t => _logger.LogError(t.Exception, "Failed to save playlist sort order"),
                    TaskContinuationOptions.OnlyOnFaulted);
            ApplyFilter();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Cleans up search state when navigating away from the page.
    /// </summary>
    public override void ResetState()
    {
        base.ResetState();
        _logger.LogDebug("Cleaned up PlaylistViewModel search resources");
    }

    private void OnPlaylistUpdated(object? sender, PlaylistUpdatedEventArgs e)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            // Update the main observable collection
            var playlist = Playlists.FirstOrDefault(p => p.Id == e.PlaylistId);
            if (playlist != null)
            {
                // Force refresh by setting to null first, then apply cache-buster
                playlist.CoverImageUri = null;
                playlist.CoverImageUri = ImageUriHelper.GetUriWithCacheBuster(e.CoverImageUri);
            }

            // Also update the backing list to keep filter/sort in sync
            var backingItem = _allPlaylists.FirstOrDefault(p => p.Id == e.PlaylistId);
            if (backingItem != null)
            {
                // Force refresh by setting to null first, then apply cache-buster
                backingItem.CoverImageUri = null;
                backingItem.CoverImageUri = ImageUriHelper.GetUriWithCacheBuster(e.CoverImageUri);
            }
        });
    }
}