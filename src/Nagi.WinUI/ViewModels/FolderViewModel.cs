using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;

namespace Nagi.WinUI.ViewModels;

/// <summary>
/// Represents a single music folder from the library in the user interface.
/// </summary>
public partial class FolderViewModelItem : ObservableObject {
    public FolderViewModelItem(Folder folder, int songCount) {
        Id = folder.Id;
        Name = folder.Name;
        Path = folder.Path;
        UpdateSongCount(songCount);
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Path { get; }

    [ObservableProperty]
    public partial int SongCount { get; set; }

    [ObservableProperty]
    public partial string SongCountText { get; set; } = string.Empty;

    /// <summary>
    /// Updates the song count and its corresponding display text.
    /// </summary>
    /// <remarks>
    /// This method avoids raising property change notifications if the count has not changed.
    /// </remarks>
    /// <param name="newSongCount">The new number of songs in the folder.</param>
    public void UpdateSongCount(int newSongCount) {
        if (SongCount == newSongCount) return;

        SongCount = newSongCount;
        SongCountText = newSongCount == 1 ? "1 song" : $"{newSongCount:N0} songs";
    }
}

/// <summary>
/// Manages the collection of music library folders and orchestrates library operations
/// such as adding, deleting, and scanning folders.
/// </summary>
public partial class FolderViewModel : ObservableObject, IDisposable {
    private readonly ILibraryService _libraryService;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly PlayerViewModel _playerViewModel;
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private bool _isDisposed;

    public FolderViewModel(ILibraryService libraryService, PlayerViewModel playerViewModel, IMusicPlaybackService musicPlaybackService, INavigationService navigationService) {
        _libraryService = libraryService;
        _playerViewModel = playerViewModel;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;

        // Store the handler in a field so we can reliably unsubscribe from it later.
        _collectionChangedHandler = (s, e) => OnPropertyChanged(nameof(HasFolders));
        Folders.CollectionChanged += _collectionChangedHandler;
    }

    [ObservableProperty]
    public partial ObservableCollection<FolderViewModelItem> Folders { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsAddingFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsDeletingFolder { get; set; }

    /// <summary>
    /// Gets a value indicating whether any long-running library operation is currently in progress.
    /// </summary>
    /// <remarks>
    /// This is used to disable UI elements to prevent concurrent conflicting operations.
    /// </remarks>
    public bool IsAnyOperationInProgress => IsAddingFolder || IsScanning || IsDeletingFolder;

    /// <summary>
    /// Gets a value indicating whether there are any folders in the library.
    /// </summary>
    public bool HasFolders => Folders.Any();

    /// <summary>
    /// Navigates to the song list for the selected folder.
    /// </summary>
    [RelayCommand]
    public void NavigateToFolderDetail(FolderViewModelItem? folder) {
        if (folder is null) return;

        var navParam = new FolderSongViewNavigationParameter {
            Title = folder.Name,
            FolderId = folder.Id
        };
        _navigationService.Navigate(typeof(FolderSongViewPage), navParam);
    }

    /// <summary>
    /// Clears the current queue and starts playing all songs from the selected folder.
    /// </summary>
    [RelayCommand]
    private async Task PlayFolderAsync(Guid folderId) {
        if (IsAnyOperationInProgress || folderId == Guid.Empty) return;

        try {
            await _musicPlaybackService.PlayFolderAsync(folderId);
        }
        catch (Exception ex) {
            _playerViewModel.GlobalOperationStatusMessage = "Error starting playback for this folder.";
            Debug.WriteLine($"[FolderViewModel] CRITICAL: Error playing folder {folderId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Asynchronously loads all folders from the database and updates the UI.
    /// </summary>
    [RelayCommand]
    private async Task LoadFoldersAsync() {
        try {
            // Fetch folder data and song counts concurrently for better performance.
            var foldersFromDb = await _libraryService.GetAllFoldersAsync();
            var folderItemTasks = foldersFromDb.Select(async folder => {
                var songCount = await _libraryService.GetSongCountForFolderAsync(folder.Id);
                return new FolderViewModelItem(folder, songCount);
            });

            var newItems = (await Task.WhenAll(folderItemTasks))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Update the UI collection efficiently without clearing and re-populating.
            SynchronizeCollection(newItems);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[FolderViewModel] ERROR: Failed to load folders: {ex.Message}");
        }
    }

    /// <summary>
    /// Efficiently synchronizes the observable collection of folders with an updated list.
    /// This method avoids clearing and re-populating the entire collection, instead calculating
    /// the differences (add, remove, move, update) to minimize UI updates and prevent flicker.
    /// </summary>
    /// <param name="newItems">The authoritative, sorted list of items that should be in the collection.</param>
    private void SynchronizeCollection(IReadOnlyList<FolderViewModelItem> newItems) {
        var currentFoldersMap = Folders.ToDictionary(f => f.Id);
        var newItemIdSet = newItems.Select(f => f.Id).ToHashSet();

        for (var i = Folders.Count - 1; i >= 0; i--) {
            var currentItem = Folders[i];
            if (!newItemIdSet.Contains(currentItem.Id)) {
                Folders.RemoveAt(i);
            }
        }

        for (var i = 0; i < newItems.Count; i++) {
            var newItem = newItems[i];

            if (i >= Folders.Count) {
                // If we are past the end of the current list, all remaining new items are added.
                Folders.Add(newItem);
                continue;
            }

            var currentItem = Folders[i];
            if (currentItem.Id == newItem.Id) {
                // The item is in the correct position, so just update its data.
                currentItem.UpdateSongCount(newItem.SongCount);
            }
            else {
                // The item at this position is incorrect. We need to either move an existing
                // item here or insert a new one.
                if (currentFoldersMap.TryGetValue(newItem.Id, out var existingItemToMove)) {
                    var oldIndex = Folders.IndexOf(existingItemToMove);
                    if (oldIndex != -1) {
                        Folders.Move(oldIndex, i);
                        Folders[i].UpdateSongCount(newItem.SongCount);
                    }
                }
                else {
                    Folders.Insert(i, newItem);
                }
            }
        }
    }

    /// <summary>
    /// Adds a new folder to the library and initiates a scan for music files.
    /// </summary>
    [RelayCommand]
    private async Task AddFolderAndScanAsync(string folderPath) {
        if (string.IsNullOrWhiteSpace(folderPath) || IsAnyOperationInProgress) return;

        if (Folders.Any(f => f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase))) {
            _playerViewModel.GlobalOperationStatusMessage = "This folder is already in the library.";
            return;
        }

        IsAddingFolder = true;
        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = "Adding folder to library...";
        _playerViewModel.GlobalOperationProgressValue = 0;
        _playerViewModel.IsGlobalOperationIndeterminate = true;

        try {
            var folder = await _libraryService.AddFolderAsync(folderPath);
            if (folder == null) {
                _playerViewModel.GlobalOperationStatusMessage = "Failed to add folder. The path may be invalid.";
                return;
            }

            IsScanning = true;

            var progress = new Progress<ScanProgress>(p => {
                _playerViewModel.GlobalOperationStatusMessage = p.StatusText;
                _playerViewModel.IsGlobalOperationIndeterminate = p.IsIndeterminate || p.Percentage < 5;
                _playerViewModel.GlobalOperationProgressValue = p.Percentage;
            });

            await _libraryService.ScanFolderForMusicAsync(folder.Path, progress);

            _playerViewModel.GlobalOperationStatusMessage = "Refreshing library...";
            await LoadFoldersAsync();
            _playerViewModel.GlobalOperationStatusMessage = $"Successfully added and scanned '{folder.Name}'.";
        }
        catch (Exception ex) {
            _playerViewModel.GlobalOperationStatusMessage = $"Error adding folder: {ex.Message}";
            Debug.WriteLine($"[FolderViewModel] ERROR: Failed to add and scan folder: {ex.Message}");
        }
        finally {
            IsAddingFolder = false;
            IsScanning = false;
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }

    /// <summary>
    /// Deletes a folder and all its associated songs from the library.
    /// </summary>
    [RelayCommand]
    private async Task DeleteFolderAsync(Guid folderId) {
        if (IsAnyOperationInProgress) return;

        var folderToDelete = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folderToDelete == null) return;

        IsDeletingFolder = true;
        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = $"Deleting '{folderToDelete.Name}'...";
        _playerViewModel.IsGlobalOperationIndeterminate = true;

        try {
            bool success = await _libraryService.RemoveFolderAsync(folderId);
            if (success) {
                Folders.Remove(folderToDelete);
                _playerViewModel.GlobalOperationStatusMessage = $"Successfully deleted '{folderToDelete.Name}'.";
            }
            else {
                _playerViewModel.GlobalOperationStatusMessage = $"Failed to delete '{folderToDelete.Name}'.";
            }
        }
        catch (Exception ex) {
            _playerViewModel.GlobalOperationStatusMessage = $"Error deleting folder: {ex.Message}";
            Debug.WriteLine($"[FolderViewModel] ERROR: Failed to delete folder: {ex.Message}");
        }
        finally {
            IsDeletingFolder = false;
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }

    /// <summary>
    /// Rescans a specific folder for new, removed, or changed music files.
    /// </summary>
    [RelayCommand]
    private async Task RescanFolderAsync(Guid folderId) {
        if (IsAnyOperationInProgress) return;

        var folderItem = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folderItem == null) return;

        IsScanning = true;
        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = $"Rescanning '{folderItem.Name}'...";
        _playerViewModel.IsGlobalOperationIndeterminate = true;

        try {
            var progress = new Progress<ScanProgress>(p => {
                _playerViewModel.GlobalOperationStatusMessage = p.StatusText;
                _playerViewModel.IsGlobalOperationIndeterminate = p.IsIndeterminate || p.Percentage < 5;
                _playerViewModel.GlobalOperationProgressValue = p.Percentage;
            });

            bool changesDetected = await _libraryService.RescanFolderForMusicAsync(folderId, progress);

            _playerViewModel.GlobalOperationStatusMessage = "Refreshing library state...";
            await LoadFoldersAsync();

            _playerViewModel.GlobalOperationStatusMessage = changesDetected
                ? $"Rescan of '{folderItem.Name}' complete."
                : $"No changes detected for '{folderItem.Name}'.";
        }
        catch (Exception ex) {
            _playerViewModel.GlobalOperationStatusMessage = $"Error rescanning folder: {ex.Message}";
            Debug.WriteLine($"[FolderViewModel] ERROR: Failed to rescan folder: {ex.Message}");
        }
        finally {
            IsScanning = false;
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }

    /// <summary>
    /// Cleans up resources by unsubscribing from event handlers.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        if (Folders != null) {
            Folders.CollectionChanged -= _collectionChangedHandler;
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}