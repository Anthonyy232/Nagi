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
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

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
        ? ResourceFormatter.Format(Nagi.Presentation.Resources.Strings.Songs_Count_Singular, SongCount)
        : ResourceFormatter.Format(Nagi.Presentation.Resources.Strings.Songs_Count_Plural, SongCount);
}

/// <summary>
///     Manages the collection of music library folders and orchestrates library operations
///     such as adding, deleting, and scanning folders.
/// </summary>
public partial class FolderViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryReader _libraryReader;
    private readonly ILibraryWriter _libraryWriter;
    private readonly ILibraryScanner _libraryScanner;
    private readonly ILogger<FolderViewModel> _logger;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly INavigationService _navigationService;
    private readonly IUIService _uiService;
    private readonly IDispatcherService _dispatcherService;
    private readonly GlobalOperationViewModel _globalOperation;
    private readonly Debouncer _debouncer = new(TimeSpan.FromSeconds(1));

    public FolderViewModel(ILibraryReader libraryReader, ILibraryWriter libraryWriter, ILibraryScanner libraryScanner, GlobalOperationViewModel globalOperation,
        IMusicPlaybackService musicPlaybackService, INavigationService navigationService, IUIService uiService,
        IDispatcherService dispatcherService, ILogger<FolderViewModel> logger)
    {
        _libraryReader = libraryReader;
        _libraryWriter = libraryWriter;
        _libraryScanner = libraryScanner;
        _globalOperation = globalOperation;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _uiService = uiService;
        _dispatcherService = dispatcherService;
        _logger = logger;

        Folders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFolders));

        _libraryScanner.LibraryContentChanged += OnLibraryContentChanged;
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
        _libraryScanner.LibraryContentChanged -= OnLibraryContentChanged;
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
        _navigationService.NavigateTo(navParam);
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
            _globalOperation.StatusMessage = Nagi.Presentation.Resources.Strings.Folders_Error_Playback;
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
            var randomFolderId = await _libraryReader.GetRandomFolderIdAsync();
            if (randomFolderId.HasValue)
            {
                await _musicPlaybackService.PlayFolderAsync(randomFolderId.Value);
            }
            else
            {
                _globalOperation.StatusMessage = Nagi.Presentation.Resources.Strings.Folders_Empty_NoMusicFound;
            }
        }
        catch (Exception ex)
        {
            _globalOperation.StatusMessage = Nagi.Presentation.Resources.Strings.Folders_Error_RandomPlayback;
            _logger.LogCritical(ex, "Error playing random folder");
        }
    }

    [RelayCommand]
    private async Task LoadFoldersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var foldersFromDb = await _libraryReader.GetRootFoldersWithCountsAsync(cancellationToken);
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

    /// <summary>
    ///     Lets the user pick a folder, then adds it to the library and scans it.
    /// </summary>
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (IsAnyOperationInProgress) return;
        var folderPath = await _uiService.PickSingleFolderAsync();
        if (folderPath is not null) await AddFolderAndScanAsync(folderPath);
    }

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
            _globalOperation.StatusMessage = Nagi.Presentation.Resources.Strings.Folders_AddFolder_Exists;
            return;
        }

        IsAddingFolder = true;
        _globalOperation.IsInProgress = true;
        _globalOperation.StatusMessage = Nagi.Presentation.Resources.Strings.Folders_AddFolder_InProgress;
        _globalOperation.ProgressValue = 0;
        _globalOperation.IsIndeterminate = true;

        try
        {
            var folder = await _libraryWriter.AddFolderAsync(folderPath);
            if (folder == null)
            {
                _globalOperation.StatusMessage = Nagi.Presentation.Resources.Strings.Folders_AddFolder_Failed;
                return;
            }

            IsScanning = true;

            var progress = new Progress<ScanProgress>(p =>
            {
                _globalOperation.StatusMessage = p.StatusText;
                _globalOperation.IsIndeterminate = p.IsIndeterminate || p.Percentage < 5;
                _globalOperation.ProgressValue = p.Percentage;
            });

            await _libraryScanner.ScanFolderForMusicAsync(folder.Path, progress);

            _globalOperation.StatusMessage = string.Format(Nagi.Presentation.Resources.Strings.Folders_AddFolder_Success, folder.Name);
        }
        catch (Exception ex)
        {
            _globalOperation.StatusMessage = string.Format(Nagi.Presentation.Resources.Strings.Folders_Error_AddFolder, ex.Message);
            _logger.LogError(ex, "Failed to add and scan folder '{FolderPath}'", folderPath);
        }
        finally
        {
            IsAddingFolder = false;
            IsScanning = false;
            _globalOperation.IsInProgress = false;
            _globalOperation.IsIndeterminate = false;
        }
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(Guid folderId)
    {
        if (IsAnyOperationInProgress) return;

        var folderToDelete = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folderToDelete == null) return;

        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Nagi.Presentation.Resources.Strings.FolderPage_DeleteDialog_Title,
            string.Format(Nagi.Presentation.Resources.Strings.FolderPage_DeleteDialog_Format, folderToDelete.Name),
            Nagi.Presentation.Resources.Strings.FolderPage_DeleteDialog_PrimaryButton,
            Nagi.Presentation.Resources.Strings.FolderPage_DeleteDialog_CloseButton);
        if (!confirmed || IsAnyOperationInProgress) return;

        IsDeletingFolder = true;
        _globalOperation.IsInProgress = true;
        _globalOperation.StatusMessage = string.Format(Nagi.Presentation.Resources.Strings.Folders_Delete_InProgress, folderToDelete.Name);
        _globalOperation.IsIndeterminate = true;

        try
        {
            var success = await _libraryWriter.RemoveFolderAsync(folderId);
            if (success)
            {
                _globalOperation.StatusMessage = string.Format(Nagi.Presentation.Resources.Strings.Folders_Delete_Success, folderToDelete.Name);
            }
            else
            {
                _globalOperation.StatusMessage = string.Format(Nagi.Presentation.Resources.Strings.Folders_Delete_Failed, folderToDelete.Name);
            }
        }
        catch (Exception ex)
        {
            _globalOperation.StatusMessage = string.Format(Nagi.Presentation.Resources.Strings.Folders_Error_DeleteFolder, ex.Message);
            _logger.LogError(ex, "Failed to delete folder {FolderId}", folderId);
        }
        finally
        {
            IsDeletingFolder = false;
            _globalOperation.IsInProgress = false;
            _globalOperation.IsIndeterminate = false;
        }
    }

    [RelayCommand]
    private async Task RescanFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        if (IsAnyOperationInProgress) return;

        var folderItem = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folderItem == null) return;

        IsScanning = true;
        _globalOperation.IsInProgress = true;
        _globalOperation.StatusMessage = string.Format(Nagi.Presentation.Resources.Strings.Folders_Rescan_InProgress, folderItem.Name);
        _globalOperation.IsIndeterminate = true;
        _globalOperation.SetCancellation(RescanFolderCommand.Cancel);

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                _globalOperation.StatusMessage = p.StatusText;
                _globalOperation.IsIndeterminate = p.IsIndeterminate || p.Percentage < 5;
                _globalOperation.ProgressValue = p.Percentage;
            });

            var changesDetected = await _libraryScanner.RescanFolderForMusicAsync(folderId, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _globalOperation.StatusMessage = changesDetected
                ? string.Format(Nagi.Presentation.Resources.Strings.Folders_Rescan_Complete, folderItem.Name)
                : string.Format(Nagi.Presentation.Resources.Strings.Folders_Rescan_NoChanges, folderItem.Name);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Folder rescan cancelled for {FolderId}.", folderId);
        }
        catch (Exception ex)
        {
            _globalOperation.StatusMessage = string.Format(Nagi.Presentation.Resources.Strings.Folders_Error_RescanFolder, ex.Message);
            _logger.LogError(ex, "Failed to rescan folder {FolderId}", folderId);
        }
        finally
        {
            _globalOperation.SetCancellation(null);
            IsScanning = false;
            _globalOperation.IsInProgress = false;
            _globalOperation.IsIndeterminate = false;
        }
    }
}
