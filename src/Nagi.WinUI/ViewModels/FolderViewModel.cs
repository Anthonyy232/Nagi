using System;
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
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.ViewModels;

public sealed class FolderViewModelItem
{
    public FolderViewModelItem(Folder folder, int songCount)
    {
        Id = folder.Id;
        Name = folder.Name;
        Path = folder.Path;
        SongCount = songCount;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Path { get; }

    public int SongCount { get; }

    public string SongCountText => SongCount == 1
        ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Songs_Count_Singular, SongCount)
        : ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Songs_Count_Plural, SongCount);
}

/// <summary>
///     Manages the collection of music library folders and orchestrates library operations
///     such as adding, deleting, and scanning folders.
/// </summary>
public partial class FolderViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService _libraryService;
    private readonly ILogger<FolderViewModel> _logger;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly IDispatcherService _dispatcherService;
    private readonly PlayerViewModel _playerViewModel;
    private readonly Debouncer _debouncer = new(TimeSpan.FromSeconds(1));

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

        Folders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFolders));

        _libraryService.LibraryContentChanged += OnLibraryContentChanged;
    }

    private void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        if (e.ChangeType == LibraryChangeType.FolderAdded ||
            e.ChangeType == LibraryChangeType.FolderRemoved ||
            e.ChangeType == LibraryChangeType.FolderRescanned ||
            e.ChangeType == LibraryChangeType.LibraryRescanned)
        {
            _debouncer.Trigger(async token =>
            {
                try
                {
                    await _dispatcherService.EnqueueAsync(() => token.IsCancellationRequested
                        ? Task.CompletedTask : LoadFoldersAsync(token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error executing debounced folder refresh");
                }
            });
        }
    }

    public void Dispose()
    {
        _libraryService.LibraryContentChanged -= OnLibraryContentChanged;
        _debouncer.Dispose();
        LoadFoldersCommand.Cancel();
    }

    public ObservableRangeCollection<FolderViewModelItem> Folders { get; } = new();

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

    public bool HasFolders => Folders.Any();


    [RelayCommand]
    public void NavigateToFolderDetail(FolderViewModelItem? folder)
    {
        if (folder is null) return;
        var navParam = new FolderSongViewNavigationParameter
        {
            Title = folder.Name,
            FolderId = folder.Id
        };
        _navigationService.Navigate(typeof(FolderSongViewPage), navParam);
    }

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

    [RelayCommand]
    private async Task LoadFoldersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var foldersFromDb = await _libraryService.GetRootFoldersWithCountsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var newItems = foldersFromDb
                .Select(item => new FolderViewModelItem(item.Folder, item.SongCount))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id)
                .ToList();

            Folders.ReplaceRange(newItems);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load folders");
        }
    }

    [RelayCommand]
    private async Task AddFolderAndScanAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || IsAnyOperationInProgress) return;

        // Pre-flight textual dedup using the shared canonicalizer. This won't collapse
        // mapped-drive vs UNC (that requires platform resolution in LibraryService), but it
        // catches case, separator, and Unicode variations so the user gets an immediate
        // "already added" message instead of a silent no-op.
        var canonicalNew = Nagi.Core.Helpers.PathCanonicalizer.Normalize(folderPath);
        if (!string.IsNullOrEmpty(canonicalNew) &&
            Folders.Any(f => Nagi.Core.Helpers.PathCanonicalizer.Normalize(f.Path)
                .Equals(canonicalNew, StringComparison.OrdinalIgnoreCase)))
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

    [RelayCommand]
    private async Task RescanFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        if (IsAnyOperationInProgress) return;

        var folderItem = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folderItem == null) return;

        IsScanning = true;
        _playerViewModel.IsGlobalOperationInProgress = true;
        _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Folders_Rescan_InProgress, folderItem.Name);
        _playerViewModel.IsGlobalOperationIndeterminate = true;
        _playerViewModel.SetGlobalOperationCancellation(RescanFolderCommand.Cancel);

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                _playerViewModel.GlobalOperationStatusMessage = p.StatusText;
                _playerViewModel.IsGlobalOperationIndeterminate = p.IsIndeterminate || p.Percentage < 5;
                _playerViewModel.GlobalOperationProgressValue = p.Percentage;
            });

            var changesDetected = await _libraryService.RescanFolderForMusicAsync(folderId, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _playerViewModel.GlobalOperationStatusMessage = changesDetected
                ? string.Format(Nagi.WinUI.Resources.Strings.Folders_Rescan_Complete, folderItem.Name)
                : string.Format(Nagi.WinUI.Resources.Strings.Folders_Rescan_NoChanges, folderItem.Name);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Folder rescan cancelled for {FolderId}.", folderId);
        }
        catch (Exception ex)
        {
            _playerViewModel.GlobalOperationStatusMessage = string.Format(Nagi.WinUI.Resources.Strings.Folders_Error_RescanFolder, ex.Message);
            _logger.LogError(ex, "Failed to rescan folder {FolderId}", folderId);
        }
        finally
        {
            _playerViewModel.SetGlobalOperationCancellation(null);
            IsScanning = false;
            _playerViewModel.IsGlobalOperationInProgress = false;
            _playerViewModel.IsGlobalOperationIndeterminate = false;
        }
    }
}
