using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Models;
using Nagi.Services;

namespace Nagi.ViewModels;

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

    [ObservableProperty] public partial int SongCount { get; set; }

    [ObservableProperty] public partial string SongCountText { get; set; } = string.Empty;

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
public partial class PlaylistViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;

    public PlaylistViewModel(ILibraryService libraryService)
    {
        _libraryService = libraryService;
        Playlists.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasPlaylists));
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
            Debug.WriteLine($"[PlaylistViewModel] CRITICAL: Error loading playlists: {ex.Message}");
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
            // BUG FIX: Use named arguments to ensure coverImageUri is passed to the correct parameter.
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
            Debug.WriteLine($"[PlaylistViewModel] CRITICAL: Error creating playlist: {ex.Message}");
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
            Debug.WriteLine($"[PlaylistViewModel] CRITICAL: Error updating playlist cover: {ex.Message}");
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
            Debug.WriteLine($"[PlaylistViewModel] CRITICAL: Error renaming playlist: {ex.Message}");
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
            Debug.WriteLine($"[PlaylistViewModel] CRITICAL: Error deleting playlist: {ex.Message}");
        }
        finally
        {
            IsDeletingPlaylist = false;
        }
    }
}