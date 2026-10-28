using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
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
        ? ResourceFormatter.Format(Strings.Songs_Count_Singular, SongCount)
        : ResourceFormatter.Format(Strings.Songs_Count_Plural, SongCount);

    public override string ToString() => Name;
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
        _globalOperation.PropertyChanged += OnGlobalOperationPropertyChanged;
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
        _globalOperation.PropertyChanged -= OnGlobalOperationPropertyChanged;
        _debouncer.Dispose();
        LoadFoldersCommand.Cancel();
    }

    public ObservableRangeCollection<FolderViewModelItem> Folders { get; } = new();

    /// <summary>
    ///     Disables folder actions while any library operation runs, including scans started from Settings.
    /// </summary>
    public bool IsAnyOperationInProgress => _globalOperation.IsInProgress;

    private void OnGlobalOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GlobalOperationViewModel.IsInProgress))
            OnPropertyChanged(nameof(IsAnyOperationInProgress));
    }

    public bool HasFolders => Folders.Any();

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
            _logger.LogError(ex, "Error playing folder {FolderId}", folderId);
            await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, Strings.Folders_Error_Playback);
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
                await _uiService.ShowMessageDialogAsync(Strings.FolderPage_Title, Strings.Folders_Empty_NoMusicFound);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing random folder");
            await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, Strings.Folders_Error_RandomPlayback);
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
        var canonicalNew = PathCanonicalizer.Normalize(folderPath);
        if (!string.IsNullOrEmpty(canonicalNew) &&
            Folders.Any(f => PathCanonicalizer.Normalize(f.Path)
                .Equals(canonicalNew, StringComparison.OrdinalIgnoreCase)))
        {
            await _uiService.ShowMessageDialogAsync(Strings.FolderPage_AddFolder_Label, Strings.Folders_AddFolder_Exists);
            return;
        }

        _globalOperation.IsInProgress = true;
        _globalOperation.StatusMessage = Strings.Folders_AddFolder_InProgress;
        _globalOperation.ProgressValue = 0;
        _globalOperation.IsIndeterminate = true;

        string? error = null;
        try
        {
            var folder = await _libraryWriter.AddFolderAsync(folderPath);
            if (folder == null)
                error = Strings.Folders_AddFolder_Failed;
            else
                await _libraryScanner.ScanFolderForMusicAsync(folder.Path, _globalOperation.CreateProgress());
        }
        catch (Exception ex)
        {
            error = string.Format(Strings.Folders_Error_AddFolder, ex.Message);
            _logger.LogError(ex, "Failed to add and scan folder '{FolderPath}'", folderPath);
        }
        finally
        {
            _globalOperation.IsInProgress = false;
            _globalOperation.IsIndeterminate = false;
        }

        if (error != null) await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, error);
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(Guid folderId)
    {
        if (IsAnyOperationInProgress) return;

        var folderToDelete = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folderToDelete == null) return;

        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Strings.FolderPage_DeleteDialog_Title,
            string.Format(Strings.FolderPage_DeleteDialog_Format, folderToDelete.Name),
            Strings.FolderPage_DeleteDialog_PrimaryButton,
            Strings.FolderPage_DeleteDialog_CloseButton);
        if (!confirmed || IsAnyOperationInProgress) return;

        _globalOperation.IsInProgress = true;
        _globalOperation.StatusMessage = string.Format(Strings.Folders_Delete_InProgress, folderToDelete.Name);
        _globalOperation.IsIndeterminate = true;

        string? error = null;
        try
        {
            if (!await _libraryWriter.RemoveFolderAsync(folderId))
                error = string.Format(Strings.Folders_Delete_Failed, folderToDelete.Name);
        }
        catch (Exception ex)
        {
            error = string.Format(Strings.Folders_Error_DeleteFolder, ex.Message);
            _logger.LogError(ex, "Failed to delete folder {FolderId}", folderId);
        }
        finally
        {
            _globalOperation.IsInProgress = false;
            _globalOperation.IsIndeterminate = false;
        }

        if (error != null) await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, error);
    }

    [RelayCommand]
    private async Task RescanFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        if (IsAnyOperationInProgress) return;

        var folderItem = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folderItem == null) return;

        _globalOperation.IsInProgress = true;
        _globalOperation.StatusMessage = string.Format(Strings.Folders_Rescan_InProgress, folderItem.Name);
        _globalOperation.IsIndeterminate = true;
        _globalOperation.SetCancellation(RescanFolderCommand.Cancel);

        string? error = null;
        try
        {
            await _libraryScanner.RescanFolderForMusicAsync(folderId, _globalOperation.CreateProgress(cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Folder rescan cancelled for {FolderId}.", folderId);
        }
        catch (Exception ex)
        {
            error = string.Format(Strings.Folders_Error_RescanFolder, ex.Message);
            _logger.LogError(ex, "Failed to rescan folder {FolderId}", folderId);
        }
        finally
        {
            _globalOperation.SetCancellation(null);
            _globalOperation.IsInProgress = false;
            _globalOperation.IsIndeterminate = false;
        }

        if (error != null) await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, error);
    }
}
