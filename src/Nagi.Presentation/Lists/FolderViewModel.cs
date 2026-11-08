using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.MusicServers;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Presentation.Common;
using Nagi.Presentation.MusicServers;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Shell;

namespace Nagi.Presentation.Lists;

public sealed class FolderViewModelItem
{
    public FolderViewModelItem(Folder folder, int songCount, bool isServer)
    {
        Id = folder.Id;
        Name = folder.Name;
        Path = folder.Path;
        SongCount = songCount;
        IsServer = isServer;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Path { get; }

    public int SongCount { get; }

    public bool IsServer { get; }

    public string SongCountText => ResourceFormatter.Format(Strings.Songs_Count, SongCount);

    public override string ToString() => Name;
}

/// <summary>
///     Manages the library's folders and music servers: adding and removing them, rescanning folders and syncing
///     servers.
/// </summary>
public partial class FolderViewModel : ClientFilteredListViewModelBase<FolderViewModelItem>, ICollectionViewModel
{
    private readonly ILibraryReader _libraryReader;
    private readonly ILibraryFolderService _libraryFolders;
    private readonly IMusicServerService _musicServers;
    private readonly IMusicPlaybackService _musicPlaybackService;
    private readonly IMusicNavigationService _navigationService;
    private readonly IUIService _uiService;
    private readonly GlobalOperationViewModel _globalOperation;

    public FolderViewModel(ILibraryReader libraryReader, ILibraryFolderService libraryFolders, ILibraryNotifier libraryNotifier,
        IMusicServerService musicServers, GlobalOperationViewModel globalOperation,
        IMusicPlaybackService musicPlaybackService, IMusicNavigationService navigationService, IUIService uiService,
        IDispatcherService dispatcherService, ILogger<FolderViewModel> logger)
        : base(libraryNotifier, dispatcherService, logger)
    {
        _libraryReader = libraryReader;
        _libraryFolders = libraryFolders;
        _musicServers = musicServers;
        _globalOperation = globalOperation;
        _musicPlaybackService = musicPlaybackService;
        _navigationService = navigationService;
        _uiService = uiService;

        _globalOperation.PropertyChanged += OnGlobalOperationPropertyChanged;
    }

    protected override bool ShouldReloadOnLibraryChange(LibraryChange change) => change.AffectsFolders;

    protected override async Task<IEnumerable<FolderViewModelItem>> LoadItemsAsync(CancellationToken token) =>
        (await _libraryReader.GetRootFoldersWithCountsAsync(token))
        .Select(item => new FolderViewModelItem(item.Folder, item.SongCount, item.IsFromServer));

    protected override IEnumerable<FolderViewModelItem> Arrange(IEnumerable<FolderViewModelItem> folders) =>
        folders.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id);

    public override void Dispose()
    {
        _globalOperation.PropertyChanged -= OnGlobalOperationPropertyChanged;
        base.Dispose();
    }

    /// <summary>
    ///     Disables folder actions while any library operation runs, including scans started from Settings.
    /// </summary>
    public bool IsAnyOperationInProgress => _globalOperation.IsInProgress;

    private void OnGlobalOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GlobalOperationViewModel.IsInProgress)) return;
        OnPropertyChanged(nameof(IsAnyOperationInProgress));
        foreach (var command in new IRelayCommand[]
                 {
                     PlayFolderCommand, PlayRandomCommand, AddFolderCommand, DeleteFolderCommand, RescanFolderCommand,
                     AddServerCommand, SyncServerCommand, SignInToServerCommand
                 })
            command.NotifyCanExecuteChanged();
    }

    private bool CanStartAction() => !IsAnyOperationInProgress;

    public void OpenFolder(FolderViewModelItem folder) => _navigationService.OpenFolder(folder.Id, folder.Name);

    void ICollectionViewModel.Open(object item) => OpenFolder((FolderViewModelItem)item);

    ISortOrderSelection? ICollectionViewModel.Sort => null;

    [RelayCommand(CanExecute = nameof(CanStartAction))]
    private async Task PlayFolderAsync(Guid folderId)
    {
        if (folderId == Guid.Empty) return;
        await UserAction.RunAsync(() => _musicPlaybackService.PlayScopeAsync(new SongScope.Folder(folderId)), _logger, _uiService,
            failureMessage: Strings.Folders_Error_Playback);
    }

    [RelayCommand(CanExecute = nameof(CanStartAction))]
    private async Task PlayRandomAsync()
    {
        await UserAction.RunAsync(async () =>
        {
            if (await _libraryReader.GetRandomFolderIdAsync() is { } folderId)
                await _musicPlaybackService.PlayScopeAsync(new SongScope.Folder(folderId));
            else
                await _uiService.ShowMessageDialogAsync(Strings.FolderPage_Title, Strings.Folders_Empty_NoMusicFound);
        }, _logger, _uiService, failureMessage: Strings.Folders_Error_RandomPlayback);
    }

    /// <summary>
    ///     Lets the user pick a folder, then adds it to the library and scans it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartAction))]
    private async Task AddFolderAsync()
    {
        var folderPath = await _uiService.PickSingleFolderAsync();
        if (folderPath is not null) await AddFolderAndScanAsync(folderPath);
    }

    private async Task AddFolderAndScanAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        // Adding a folder that is already in the library hands it back, so say so here rather than rescanning it.
        var canonicalNew = PathCanonicalizer.Normalize(folderPath);
        if (AllItems.Any(f => PathCanonicalizer.Normalize(f.Path).Equals(canonicalNew, StringComparison.OrdinalIgnoreCase)))
        {
            await _uiService.ShowMessageDialogAsync(Strings.FolderPage_AddFolder_Label, Strings.Folders_AddFolder_Exists);
            return;
        }

        var title = Strings.Generic_Error;
        string? error = null;
        using (var operation = _globalOperation.TryBegin(Strings.Folders_AddFolder_InProgress))
        {
            if (operation is null) return;
            try
            {
                var folder = await _libraryFolders.AddFolderAsync(folderPath);
                if (folder == null)
                    error = Strings.Folders_AddFolder_Failed;
                else
                    await _libraryFolders.RescanFolderForMusicAsync(folder.Id, _globalOperation.CreateProgress());
            }
            catch (FolderOverlapsException)
            {
                title = Strings.FolderPage_AddFolder_Label;
                error = Strings.Folders_AddFolder_Overlaps;
            }
            catch (Exception ex)
            {
                error = ResourceFormatter.Format(Strings.Folders_Error_AddFolder, ex.Message);
                _logger.LogError(ex, "Failed to add and scan folder '{FolderPath}'", folderPath);
            }
        }

        if (error != null) await _uiService.ShowMessageDialogAsync(title, error);
    }

    [RelayCommand(CanExecute = nameof(CanStartAction))]
    private async Task DeleteFolderAsync(Guid folderId)
    {
        var folderToDelete = AllItems.FirstOrDefault(f => f.Id == folderId);
        if (folderToDelete == null) return;

        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            folderToDelete.IsServer ? Strings.FolderPage_RemoveServerDialog_Title : Strings.FolderPage_DeleteDialog_Title,
            ResourceFormatter.Format(folderToDelete.IsServer ? Strings.FolderPage_RemoveServerDialog_Format : Strings.FolderPage_DeleteDialog_Format,
                folderToDelete.Name),
            Strings.FolderPage_DeleteDialog_PrimaryButton,
            Strings.Generic_Cancel);
        if (!confirmed) return;

        string? error = null;
        using (var operation = _globalOperation.TryBegin(ResourceFormatter.Format(Strings.Folders_Delete_InProgress, folderToDelete.Name)))
        {
            if (operation is null) return;
            try
            {
                var removed = folderToDelete.IsServer
                    ? await _musicServers.RemoveAsync(folderId)
                    : await _libraryFolders.RemoveFolderAsync(folderId);
                if (!removed)
                    error = ResourceFormatter.Format(Strings.Folders_Delete_Failed, folderToDelete.Name);
            }
            catch (Exception ex)
            {
                error = ResourceFormatter.Format(Strings.Folders_Error_DeleteFolder, ex.Message);
                _logger.LogError(ex, "Failed to delete folder {FolderId}", folderId);
            }
        }

        if (error != null) await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, error);
    }

    [RelayCommand(CanExecute = nameof(CanStartAction))]
    private async Task RescanFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        var folderItem = AllItems.FirstOrDefault(f => f.Id == folderId);
        if (folderItem == null) return;

        string? error = null;
        using (var operation = _globalOperation.TryBegin(ResourceFormatter.Format(Strings.Folders_Rescan_InProgress, folderItem.Name),
                   RescanFolderCommand.Cancel))
        {
            if (operation is null) return;
            try
            {
                await _libraryFolders.RescanFolderForMusicAsync(folderId, _globalOperation.CreateProgress(cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Folder rescan cancelled for {FolderId}.", folderId);
            }
            catch (Exception ex)
            {
                error = ResourceFormatter.Format(Strings.Folders_Error_RescanFolder, ex.Message);
                _logger.LogError(ex, "Failed to rescan folder {FolderId}", folderId);
            }
        }

        if (error != null) await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, error);
    }

    /// <summary>
    ///     Lets the user add a music server, then syncs its songs into the library.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartAction))]
    private async Task AddServerAsync()
    {
        if (await _uiService.ShowAddMusicServerDialogAsync() is { } server) await RunServerSyncAsync(server.Id, server.Name);
    }

    [RelayCommand(CanExecute = nameof(CanStartAction))]
    private async Task SyncServerAsync(Guid folderId)
    {
        if (AllItems.FirstOrDefault(f => f.Id == folderId) is not { } server) return;
        await RunServerSyncAsync(server.Id, server.Name);
    }

    [RelayCommand(CanExecute = nameof(CanStartAction))]
    private async Task SignInToServerAsync(Guid folderId)
    {
        if (AllItems.FirstOrDefault(f => f.Id == folderId) is not { } server) return;
        if (await _uiService.ShowMusicServerSignInDialogAsync(folderId)) await RunServerSyncAsync(server.Id, server.Name);
    }

    private Task RunServerSyncAsync(Guid folderId, string name) =>
        MusicServerErrors.RunAsync(_uiService, _logger, folderId, name, () => SyncServerWithProgressAsync(folderId, name),
            ResourceFormatter.Format(Strings.Folders_Sync_Error_Failed, name));

    private async Task SyncServerWithProgressAsync(Guid folderId, string name)
    {
        using var syncCts = new CancellationTokenSource();
        using var operation = _globalOperation.TryBegin(ResourceFormatter.Format(Strings.Folders_Sync_InProgress, name), syncCts.Cancel);
        if (operation is null) return;
        await _musicServers.SyncAsync(folderId, _globalOperation.CreateProgress(syncCts.Token), syncCts.Token);
    }
}
