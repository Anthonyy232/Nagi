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
        CoverImageUri = playlist.CoverImageUri;
        IsSmart = false;
        DateCreated = playlist.DateCreated;
        DateModified = playlist.DateModified;
        UpdateSongCount(playlist.PlaylistSongs?.Count ?? 0);
    }

    public PlaylistViewModelItem(SmartPlaylist smartPlaylist, int matchingSongCount = 0)
    {
        Id = smartPlaylist.Id;
        Name = smartPlaylist.Name;
        CoverImageUri = smartPlaylist.CoverImageUri;
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

    [ObservableProperty] public partial int SongCount { get; set; }

    [ObservableProperty] public partial string SongCountText { get; set; } = string.Empty;

    partial void OnCoverImageUriChanged(string? value)
    {
        OnPropertyChanged(nameof(IsArtworkAvailable));
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
            SongCountText = newSongCount == 1 ? "Smart • 1 song" : $"Smart • {newSongCount} songs";
        }
        else
        {
            SongCountText = newSongCount == 1 ? "1 song" : $"{newSongCount} songs";
        }
    }
}

/// <summary>
///     ViewModel for managing the collection of playlists.
/// </summary>
public partial class PlaylistViewModel : ObservableObject, IDisposable
{
    private const int SearchDebounceDelay = 300;
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILibraryService _libraryService;
    private readonly ISmartPlaylistService _smartPlaylistService;
    private readonly ILogger<PlaylistViewModel> _logger;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private CancellationTokenSource? _debounceCts;
    private bool _isDisposed;
    private List<PlaylistViewModelItem> _allPlaylists = new();

    public PlaylistViewModel(ILibraryService libraryService, ISmartPlaylistService smartPlaylistService,
        IMusicPlaybackService musicPlaybackService, INavigationService navigationService,
        IDispatcherService dispatcherService, ILogger<PlaylistViewModel> logger)
    {
        _libraryService = libraryService;
        _smartPlaylistService = smartPlaylistService;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _dispatcherService = dispatcherService;
        _logger = logger;

        // Store the handler in a field so we can reliably unsubscribe from it later.
        _collectionChangedHandler = (s, e) => OnPropertyChanged(nameof(HasPlaylists));
        Playlists.CollectionChanged += _collectionChangedHandler;
    }

    [ObservableProperty] public partial ObservableCollection<PlaylistViewModelItem> Playlists { get; set; } = new();

    [ObservableProperty] public partial string SearchTerm { get; set; } = string.Empty;

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

    [ObservableProperty] public partial string CurrentSortOrderText { get; set; } = SortOrderHelper.AToZ;

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);

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
    ///     Cleans up resources by unsubscribing from event handlers.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        if (Playlists != null) Playlists.CollectionChanged -= _collectionChangedHandler;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Navigates to the song list for the selected playlist.
    /// </summary>
    [RelayCommand]
    public void NavigateToPlaylistDetail(PlaylistViewModelItem? playlist)
    {
        if (playlist is null) return;

        if (playlist.IsSmart)
        {
            var navParam = new SmartPlaylistSongViewNavigationParameter
            {
                Title = playlist.Name,
                SmartPlaylistId = playlist.Id
            };
            _navigationService.Navigate(typeof(SmartPlaylistSongViewPage), navParam);
        }
        else
        {
            var navParam = new PlaylistSongViewNavigationParameter
            {
                Title = playlist.Name,
                PlaylistId = playlist.Id
            };
            _navigationService.Navigate(typeof(PlaylistSongViewPage), navParam);
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

        StatusMessage = "Starting playlist...";
        try
        {
            if (isSmart)
            {
                var songs = await _smartPlaylistService.GetMatchingSongsAsync(playlistId);
                var songList = songs.ToList();
                if (songList.Count == 0)
                {
                    StatusMessage = "No songs match this smart playlist's rules.";
                    _logger.LogDebug("Smart playlist {PlaylistId} has no matching songs", playlistId);
                    return;
                }
                await _musicPlaybackService.PlayAsync(songList);
            }
            else
            {
                await _musicPlaybackService.PlayPlaylistAsync(playlistId);
            }
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = "Error starting playback for this playlist.";
            _logger.LogCritical(ex, "Error playing playlist {PlaylistId}", playlistId);
        }
    }

    /// <summary>
    ///     Loads all playlists (both regular and smart) from the services.
    /// </summary>
    [RelayCommand]
    private async Task LoadPlaylistsAsync()
    {
        StatusMessage = "Loading playlists...";
        try
        {
            Playlists.Clear();
            _allPlaylists.Clear();

            // Load regular playlists
            var playlistsFromDb = await _libraryService.GetAllPlaylistsAsync();
            foreach (var playlist in playlistsFromDb)
                _allPlaylists.Add(new PlaylistViewModelItem(playlist));

            // Load smart playlists with their match counts (batch operation for performance)
            var smartPlaylistsFromDb = await _smartPlaylistService.GetAllSmartPlaylistsAsync();
            var matchCounts = await _smartPlaylistService.GetAllMatchingSongCountsAsync();
            
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
            StatusMessage = "Error loading playlists.";
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
        StatusMessage = "Creating new playlist...";

        try
        {
            var newPlaylist =
                await _libraryService.CreatePlaylistAsync(playlistName.Trim(), coverImageUri: coverImageUri);
            if (newPlaylist != null)
            {
                Playlists.Add(new PlaylistViewModelItem(newPlaylist));
                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = "Failed to create playlist. It may already exist.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "An error occurred while creating the playlist.";
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
        StatusMessage = "Updating playlist cover...";

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
                if (playlistItem != null) playlistItem.CoverImageUri = newCoverImageUri;
                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = "Failed to update playlist cover.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "An error occurred while updating the playlist cover.";
            _logger.LogError(ex, "Error updating playlist cover for {PlaylistId}", playlistId);
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
        StatusMessage = "Renaming playlist...";

        try
        {
            bool success;
            if (isSmart)
                success = await _smartPlaylistService.RenameSmartPlaylistAsync(playlistId, newName.Trim());
            else
                success = await _libraryService.RenamePlaylistAsync(playlistId, newName.Trim());
            
            if (success)
            {
                var playlistItem = Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (playlistItem != null) playlistItem.Name = newName.Trim();
                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = "Failed to rename playlist.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "An error occurred while renaming the playlist.";
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
    [RelayCommand]
    private async Task DeletePlaylistAsync(Tuple<Guid, bool> args)
    {
        var (playlistId, isSmart) = args;
        if (IsAnyOperationInProgress) return;

        IsDeletingPlaylist = true;
        StatusMessage = "Deleting playlist...";

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
                StatusMessage = "Failed to delete playlist.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "An error occurred while deleting the playlist.";
            _logger.LogError(ex, "Error deleting playlist {PlaylistId}", playlistId);
        }
        finally
        {
            IsDeletingPlaylist = false;
        }
    }

    partial void OnSearchTermChanged(string value)
    {
        TriggerDebouncedSearch();
    }

    private void TriggerDebouncedSearch()
    {
        try
        {
            _debounceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore exception if the CancellationTokenSource has already been disposed.
        }

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, token);

                if (token.IsCancellationRequested) return;

                await _dispatcherService.EnqueueAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                        ApplyFilter();
                    return Task.CompletedTask;
                });
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug("Debounced playlist search cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Debounced playlist search failed");
            }
        }, token);
    }

    private void ApplyFilter()
    {
        Playlists.Clear();

        IEnumerable<PlaylistViewModelItem> filtered = _allPlaylists;
        if (IsSearchActive)
            filtered = _allPlaylists.Where(p =>
                p.Name?.Contains(SearchTerm, StringComparison.OrdinalIgnoreCase) == true);

        // Apply sort order
        var sorted = CurrentSortOrder switch
        {
            PlaylistSortOrder.NameDesc => filtered.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase),
            PlaylistSortOrder.DateCreatedDesc => filtered.OrderByDescending(p => p.DateCreated).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            PlaylistSortOrder.DateCreatedAsc => filtered.OrderBy(p => p.DateCreated).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            PlaylistSortOrder.DateModifiedDesc => filtered.OrderByDescending(p => p.DateModified).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        };

        foreach (var item in sorted)
            Playlists.Add(item);
    }

    /// <summary>
    ///     Changes the sort order and reapplies filtering.
    /// </summary>
    [RelayCommand]
    public void ChangeSortOrder(string sortOrderString)
    {
        if (Enum.TryParse<PlaylistSortOrder>(sortOrderString, out var newSortOrder)
            && newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            UpdateSortOrderText();
            ApplyFilter();
        }
    }

    private void UpdateSortOrderText()
    {
        CurrentSortOrderText = SortOrderHelper.GetDisplayName(CurrentSortOrder);
    }

    /// <summary>
    ///     Cleans up search state when navigating away from the page.
    /// </summary>
    public void Cleanup()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
        SearchTerm = string.Empty;
        _logger.LogDebug("Cleaned up PlaylistViewModel search resources");
    }
}