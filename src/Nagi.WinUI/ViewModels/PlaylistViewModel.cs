using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Represents a single playlist item for display in the UI.
/// </summary>
public partial class PlaylistViewModelItem : ObservableObject
{
    public PlaylistViewModelItem(Playlist playlist)
    {
        Id = playlist.Id;
        Name = playlist.Name;
        CoverImageUri = playlist.CoverImageUri;
        UpdateSongCount(playlist.PlaylistSongs?.Count ?? 0);
    }

    public Guid Id { get; }

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
        SongCountText = newSongCount == 1 ? "1 song" : $"{newSongCount} songs";
    }
}

/// <summary>
///     ViewModel for managing the collection of playlists.
/// </summary>
public partial class PlaylistViewModel : ObservableObject, IDisposable
{
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly ILibraryService _libraryService;
    private readonly ILogger<PlaylistViewModel> _logger;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private bool _isDisposed;

    public PlaylistViewModel(ILibraryService libraryService, IMusicPlaybackService musicPlaybackService,
        INavigationService navigationService, ILogger<PlaylistViewModel> logger)
    {
        _libraryService = libraryService;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _logger = logger;

        // Store the handler in a field so we can reliably unsubscribe from it later.
        _collectionChangedHandler = (s, e) => OnPropertyChanged(nameof(HasPlaylists));
        Playlists.CollectionChanged += _collectionChangedHandler;
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

    /// <summary>
    ///     Gets a value indicating whether any background operation is in progress.
    /// </summary>
    public bool IsAnyOperationInProgress =>
        IsCreatingPlaylist || IsRenamingPlaylist || IsDeletingPlaylist || IsUpdatingCover;

    /// <summary>
    ///     Gets a value indicating whether there are any playlists in the library.
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

        var navParam = new PlaylistSongViewNavigationParameter
        {
            Title = playlist.Name,
            PlaylistId = playlist.Id
        };
        _navigationService.Navigate(typeof(PlaylistSongViewPage), navParam);
    }

    /// <summary>
    ///     Clears the current queue and starts playing the selected playlist.
    /// </summary>
    [RelayCommand]
    private async Task PlayPlaylistAsync(Guid playlistId)
    {
        if (IsAnyOperationInProgress || playlistId == Guid.Empty) return;

        StatusMessage = "Starting playlist...";
        try
        {
            await _musicPlaybackService.PlayPlaylistAsync(playlistId);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = "Error starting playback for this playlist.";
            _logger.LogCritical(ex, "Error playing playlist {PlaylistId}", playlistId);
        }
    }

    /// <summary>
    ///     Loads all playlists from the library service.
    /// </summary>
    [RelayCommand]
    private async Task LoadPlaylistsAsync()
    {
        StatusMessage = "Loading playlists...";
        try
        {
            var playlistsFromDb = await _libraryService.GetAllPlaylistsAsync();
            Playlists.Clear();
            foreach (var playlist in playlistsFromDb.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                Playlists.Add(new PlaylistViewModelItem(playlist));
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
    ///     Updates the cover image for an existing playlist.
    /// </summary>
    /// <param name="args">A tuple containing the playlist ID and the new cover image URI.</param>
    [RelayCommand]
    private async Task UpdatePlaylistCoverAsync(Tuple<Guid, string> args)
    {
        var (playlistId, newCoverImageUri) = args;
        if (string.IsNullOrWhiteSpace(newCoverImageUri) || IsAnyOperationInProgress) return;

        IsUpdatingCover = true;
        StatusMessage = "Updating playlist cover...";

        try
        {
            var success = await _libraryService.UpdatePlaylistCoverAsync(playlistId, newCoverImageUri);
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
    ///     Renames an existing playlist.
    /// </summary>
    /// <param name="args">A tuple containing the playlist ID and the new name.</param>
    [RelayCommand]
    private async Task RenamePlaylistAsync(Tuple<Guid, string> args)
    {
        var (playlistId, newName) = args;
        if (string.IsNullOrWhiteSpace(newName) || IsAnyOperationInProgress) return;

        IsRenamingPlaylist = true;
        StatusMessage = "Renaming playlist...";

        try
        {
            var success = await _libraryService.RenamePlaylistAsync(playlistId, newName.Trim());
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
    ///     Deletes a playlist.
    /// </summary>
    [RelayCommand]
    private async Task DeletePlaylistAsync(Guid playlistId)
    {
        if (IsAnyOperationInProgress) return;

        IsDeletingPlaylist = true;
        StatusMessage = "Deleting playlist...";

        try
        {
            var success = await _libraryService.DeletePlaylistAsync(playlistId);
            if (success)
            {
                var playlistItem = Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (playlistItem != null) Playlists.Remove(playlistItem);
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
}