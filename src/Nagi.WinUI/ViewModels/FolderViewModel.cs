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

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Represents a single music folder from the library in the user interface.
/// </summary>
public partial class FolderViewModelItem : ObservableObject
{
    public FolderViewModelItem(Folder folder, int songCount)
    {
        Id = folder.Id;
        Name = folder.Name;
        Path = folder.Path;
        UpdateSongCount(songCount);
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Path { get; }

    [ObservableProperty] public partial int SongCount { get; set; }

    [ObservableProperty] public partial string SongCountText { get; set; } = string.Empty;

    /// <summary>
    ///     Updates the song count and its corresponding display text.
    /// </summary>
    /// <remarks>
    ///     This method avoids raising property change notifications if the count has not changed.
    /// </remarks>
    /// <param name="newSongCount">The new number of songs in the folder.</param>
    public void UpdateSongCount(int newSongCount)
    {
        if (SongCount == newSongCount) return;

        SongCount = newSongCount;
        SongCountText = newSongCount == 1
            ? string.Format(Nagi.WinUI.Resources.Strings.Songs_Count_Singular, newSongCount)
            : string.Format(Nagi.WinUI.Resources.Strings.Songs_Count_Plural, newSongCount);
    }
}

/// <summary>
///     Manages the collection of music library folders and orchestrates library operations
///     such as adding, deleting, and scanning folders.
/// </summary>
public partial class FolderViewModel : ObservableObject
{
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly ILibraryService _libraryService;
    private readonly ILogger<FolderViewModel> _logger;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly IDispatcherService _dispatcherService;
    private readonly PlayerViewModel _playerViewModel;
    private CancellationTokenSource? _debouncer;
    private bool _isNavigating;

    public FolderViewModel(ILibraryService libraryService, PlayerViewModel playerViewModel,
        IMusicPlaybackService musicPlaybackService, INavigationService navigationService,
        IDispatcherService dispatcherService, ILogger<FolderViewModel> logger)
    {
        _libraryService = libraryService;
        _playerViewModel = playerViewModel;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _dispatcherService = dispatcherService;
        _logger = logger;

        // Store handler reference to enable proper cleanup in Dispose
        _collectionChangedHandler = (s, e) => OnPropertyChanged(nameof(HasFolders));
        Folders.CollectionChanged += _collectionChangedHandler;
        
        _libraryService.LibraryContentChanged += OnLibraryContentChanged;
    }

    private void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        // Refresh folders list on any folder-related change
        if (e.ChangeType == LibraryChangeType.FolderAdded || 
            e.ChangeType == LibraryChangeType.FolderRemoved || 
            e.ChangeType == LibraryChangeType.FolderRescanned ||
            e.ChangeType == LibraryChangeType.LibraryRescanned)
        {
            // Debounce to prevent multiple refresh calls during rapid changes.
            // We exchange the CTS to ensure only the latest one survives.
            var oldCts = Interlocked.Exchange(ref _debouncer, new CancellationTokenSource());
            
            try
            {
                oldCts?.Cancel();
                // We do NOT dispose oldCts here immediately. 
                // There is a race condition where the Task using the token might check .IsCancellationRequested 
                // at the exact moment we dispose it, causing an ObjectDisposedException on a background thread.
                // Letting GC handle the disposal is safer for this high-frequency transient object, 
                // or we could schedule disposal, but suppressing finalization isn't critical for simple CTS.
            }
            catch (ObjectDisposedException) { }

            var token = _debouncer.Token;

            Task.Run(async () =>
            {
                try
                {
                    // 1 second is enough to catch all events from a folder scan while still feeling responsive
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    
                    if (token.IsCancellationRequested) return;
                    
                    await _dispatcherService.EnqueueAsync(() => LoadFoldersAsync());
                }
                catch (OperationCanceledException)
                {
                    // Expected when a new event arrives
                }
                catch (ObjectDisposedException)
                {
                    // Expected if the CTS was disposed immediately after cancellation in a race
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing debounced folder refresh");
                }
            }, token);
        }
    }

    [ObservableProperty] public partial ObservableCollection<FolderViewModelItem> Folders { get; set; } = new();

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
    ///     Gets a value indicating whether any long-running library operation is currently in progress.
    /// </summary>
    /// <remarks>
    ///     This is used to disable UI elements to prevent concurrent conflicting operations.
    /// </remarks>
    public bool IsAnyOperationInProgress => IsAddingFolder || IsScanning || IsDeletingFolder;

    /// <summary>
    ///     Gets a value indicating whether there are any folders in the library.
    /// </summary>
    public bool HasFolders => Folders.Any();


    /// <summary>
    ///     Navigates to the song list for the selected folder.
    /// </summary>
    [RelayCommand]
    public void NavigateToFolderDetail(FolderViewModelItem? folder)
    {
        if (folder is null || _isNavigating) return;

        _isNavigating = true;
        try
        {
            var navParam = new FolderSongViewNavigationParameter
            {
                Title = folder.Name,
                FolderId = folder.Id
            };
            _navigationService.Navigate(typeof(FolderSongViewPage), navParam);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    /// <summary>
    ///     Clears the current queue and starts playing all songs from the selected folder.
    /// </summary>
    [RelayCommand]
    private async Task PlayFolderAsync(Guid folderId)
    {
        if (IsAnyOperationInProgress || folderId == Guid.Empty) return;

        try
        {
            await _musicPlaybackService.PlayFolderAsync(folderId);
        }
        catch (Exception ex)
        {
            _playerViewModel.GlobalOperationStatusMessage = Nagi.WinUI.Resources.Strings.Folders_Error_Playback;
            _logger.LogCritical(ex, "Error playing folder {FolderId}", folderId);
        }
    }

    /// <summary>
    ///     Fetches a random folder ID (one that contains songs) and starts playback.
    /// </summary>
    [RelayCommand]
    private async Task PlayRandomFolderAsync()
    {
        if (IsAnyOperationInProgress) return;

        try
        {
            // The service method already filters for folders that contain songs
            var randomFolderId = await _libraryService.GetRandomFolderIdAsync();
            if (randomFolderId.HasValue)
            {
                await _musicPlaybackService.PlayFolderAsync(randomFolderId.Value);
            }
            else
            {
                _playerViewModel.GlobalOperationStatusMessage = Nagi.WinUI.Resources.Strings.Folders_Empty_NoMusicFound;
            }
        }
        catch (Exception ex)
        {
            _playerViewModel.GlobalOperationStatusMessage = Nagi.WinUI.Resources.Strings.Folders_Error_RandomPlayback;
            _logger.LogCritical(ex, "Error playing random folder");
        }
    }

    /// <summary>
    ///     Asynchronously loads all root folders from the database and updates the UI.
    /// </summary>
    [RelayCommand]
    private async Task LoadFoldersAsync()
    {
        try
        {
            var foldersFromDb = await _libraryService.GetRootFoldersAsync();
            var folderItemTasks = foldersFromDb.Select(async folder =>
            {
                var songCount = await _libraryService.GetSongCountForFolderAsync(folder.Id);
                return new FolderViewModelItem(folder, songCount);
            });

            var newItems = (await Task.WhenAll(folderItemTasks))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id)
                .ToList();

            // Update the UI collection efficiently without clearing and re-populating.
            SynchronizeCollection(newItems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load folders");
        }
    }

    /// <summary>
    ///     Efficiently updates the folder collection by calculating diffs rather than rebuilding,
    ///     minimizing UI updates and preventing visual flicker.
    /// </summary>
    /// <param name="newItems">The authoritative, sorted list of items that should be in the collection.</param>
    private void SynchronizeCollection(IReadOnlyList<FolderViewModelItem> newItems)
    {
        var currentFoldersMap = Folders.ToDictionary(f => f.Id);
        var newItemIdSet = newItems.Select(f => f.Id).ToHashSet();

        for (var i = Folders.Count - 1; i >= 0; i--)
        {
            var currentItem = Folders[i];
            if (!newItemIdSet.Contains(currentItem.Id)) Folders.RemoveAt(i);
        }

        for (var i = 0; i < newItems.Count; i++)
        {
            var newItem = newItems[i];

            if (i >= Folders.Count)
            {
                Folders.Add(newItem);
                continue;
            }

            var currentItem = Folders[i];
            if (currentItem.Id == newItem.Id)
            {
                currentItem.UpdateSongCount(newItem.SongCount);
            }
            else
            {
                if (currentFoldersMap.TryGetValue(newItem.Id, out var existingItemToMove))
                {
                    var oldIndex = Folders.IndexOf(existingItemToMove);
                    if (oldIndex != -1)
                    {
                        Folders.Move(oldIndex, i);
                        Folders[i].UpdateSongCount(newItem.SongCount);
                    }
                }
                else
                {
                    Folders.Insert(i, newItem);
                }
            }
        }
    }

    /// <summary>
    ///     Adds a new folder to the library and initiates a scan for music files.
    /// </summary>
    [RelayCommand]
    private async Task AddFolderAndScanAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || IsAnyOperationInProgress) return;

        if (Folders.Any(f => f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            _playerViewModel.GlobalOperationStatusMessage = Nagi.WinUI.Resources.Strings.Folders_AddFolder_Exists;
            return;
        }

        IsAddingFolder = true;
        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = Nagi.WinUI.Resources.Strings.Folders_AddFolder_InProgress;
        _playerViewModel.GlobalOperationProgressValue = 0;
        _playerViewModel.IsGlobalOperationIndeterminate = true;

        try
        {
            var folder = await _libraryService.AddFolderAsync(folderPath);
            if (folder == null)
            {
                _playerViewModel.GlobalOperationStatusMessage = Nagi.WinUI.Resources.Strings.Folders_AddFolder_Failed;
                return;
            }

            IsScanning = true;

            var progress = new Progress<ScanProgress>(p =>
            {
                _playerViewModel.GlobalOperationStatusMessage = p.StatusText;
                _playerViewModel.IsGlobalOperationIndeterminate = p.IsIndeterminate || p.Percentage < 5;
                _playerViewModel.GlobalOperationProgressValue = p.Percentage;
            });

            await _libraryService.ScanFolderForMusicAsync(folder.Path, progress);

            _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Folders_AddFolder_Success, folder.Name);
        }
        catch (Exception ex)
        {
            _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Folders_Error_AddFolder, ex.Message);
            _logger.LogError(ex, "Failed to add and scan folder '{FolderPath}'", folderPath);
        }
        finally
        {
            IsAddingFolder = false;
            IsScanning = false;
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }

    /// <summary>
    ///     Deletes a folder and all its associated songs from the library.
    /// </summary>
    [RelayCommand]
    private async Task DeleteFolderAsync(Guid folderId)
    {
        if (IsAnyOperationInProgress) return;

        var folderToDelete = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folderToDelete == null) return;

        IsDeletingFolder = true;
        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Folders_Delete_InProgress, folderToDelete.Name);
        _playerViewModel.IsGlobalOperationIndeterminate = true;

        try
        {
            var success = await _libraryService.RemoveFolderAsync(folderId);
            if (success)
            {
                _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Folders_Delete_Success, folderToDelete.Name);
            }
            else
            {
                _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Folders_Delete_Failed, folderToDelete.Name);
            }
        }
        catch (Exception ex)
        {
            _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Folders_Error_DeleteFolder, ex.Message);
            _logger.LogError(ex, "Failed to delete folder {FolderId}", folderId);
        }
        finally
        {
            IsDeletingFolder = false;
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }

    /// <summary>
    ///     Rescans a specific folder for new, removed, or changed music files.
    /// </summary>
    [RelayCommand]
    private async Task RescanFolderAsync(Guid folderId)
    {
        if (IsAnyOperationInProgress) return;

        var folderItem = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folderItem == null) return;

        IsScanning = true;
        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Folders_Rescan_InProgress, folderItem.Name);
        _playerViewModel.IsGlobalOperationIndeterminate = true;

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                _playerViewModel.GlobalOperationStatusMessage = p.StatusText;
                _playerViewModel.IsGlobalOperationIndeterminate = p.IsIndeterminate || p.Percentage < 5;
                _playerViewModel.GlobalOperationProgressValue = p.Percentage;
            });

            var changesDetected = await _libraryService.RescanFolderForMusicAsync(folderId, progress);

            _playerViewModel.GlobalOperationStatusMessage = changesDetected
                ? string.Format(Nagi.WinUI.Resources.Strings.Folders_Rescan_Complete, folderItem.Name)
                : string.Format(Nagi.WinUI.Resources.Strings.Folders_Rescan_NoChanges, folderItem.Name);
        }
        catch (Exception ex)
        {
            _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Folders_Error_RescanFolder, ex.Message);
            _logger.LogError(ex, "Failed to rescan folder {FolderId}", folderId);
        }
        finally
        {
            IsScanning = false;
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }
}