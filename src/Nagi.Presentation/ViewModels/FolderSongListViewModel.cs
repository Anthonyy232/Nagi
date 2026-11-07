using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Models;
using Nagi.Core.Playback;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;

namespace Nagi.Presentation.ViewModels;

public sealed class BreadcrumbItem
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool IsLast { get; set; }

    public override string ToString() => Name;
}

/// <summary>
///     Provides data and commands for displaying the contents (folders and songs) within a specific library folder or
///     directory. Folders and songs are paged together in a single virtual list: folders appear first, then songs.
///     Supports hierarchical navigation through subfolders and folder-name search.
/// </summary>
public partial class FolderSongListViewModel : DetailSongListViewModelBase<FolderSongViewNavigationParameter>
{
    private string _currentDirectoryPath = string.Empty;
    private Guid? _rootFolderId;
    private string? _rootFolderPath;
    private readonly List<Folder> _selectedFolders = new();

    private bool _isNavigating;

    public FolderSongListViewModel(
        ILibraryReader libraryReader,
        IListeningService listening,
        ILibraryNotifier libraryNotifier,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        ISettingsService settingsService,
        IUIService uiService,
        ILogger<FolderSongListViewModel> logger)
        : base(libraryReader, listening, libraryNotifier, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger, CommonSortOrders, AppSettings.FolderViewSortOrder)
    {
    }

    public ObservableRangeCollection<FolderContentItem> FolderContents { get; } = new();

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();

    [ObservableProperty]
    public partial bool IsAtRootLevel { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderLabel), nameof(EmptyTitle), nameof(EmptySubtitle))]
    public partial bool IsServer { get; private set; }

    public string HeaderLabel => IsServer ? Strings.Label_Server : Strings.FolderSongViewPage_Header_Label;

    public string EmptyTitle => IsServer ? Strings.FolderSongViewPage_ServerEmpty_Title : Strings.FolderSongViewPage_Empty_Title;

    public string EmptySubtitle =>
        IsServer ? Strings.FolderSongViewPage_ServerEmpty_Subtitle : Strings.FolderSongViewPage_Empty_Subtitle;

    protected override async Task<bool> OpenDetailsAsync(FolderSongViewNavigationParameter parameter, CancellationToken token)
    {
        _rootFolderId = parameter.FolderId;
        _rootFolderPath = null;
        _currentDirectoryPath = string.Empty;

        var rootFolder = await _libraryReader.GetFolderByIdAsync(parameter.FolderId);
        if (token.IsCancellationRequested) return true;

        _rootFolderPath = rootFolder?.Path;
        IsServer = rootFolder?.MusicServer is not null;
        _currentDirectoryPath = _rootFolderPath ?? string.Empty;
        PageTitle = parameter.Title;
        UpdateBreadcrumbs();
        return true;
    }

    protected override void ClearDetails() => FolderContents.Clear();

    private SongScope.Directory? CurrentDirectory => _rootFolderId is { } rootFolderId && !string.IsNullOrEmpty(_currentDirectoryPath)
        ? new SongScope.Directory(rootFolderId, _currentDirectoryPath)
        : null;

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default) =>
        CurrentDirectory is { } directory
            ? await _libraryReader.GetDirectoryPageAsync(directory, SearchTerm, sortOrder, pageNumber, pageSize, cancellationToken)
            : new DirectoryPage();

    protected override void ApplyItemsToCollection(PagedResult<Song> result, bool append)
    {
        var page = (DirectoryPage)result;
        FolderContents.AppendOrReplace(
            page.Folders.Select(FolderContentItem.FromFolder).Concat(page.Items.Select(FolderContentItem.FromSong)), append);
        base.ApplyItemsToCollection(result, append);
    }

    protected override string FormatTotalItemsText(PagedResult<Song> result)
    {
        var page = (DirectoryPage)result;
        var parts = new List<string>();
        if (page.FolderCount > 0)
            parts.Add(ResourceFormatter.Format(page.FolderCount == 1
                ? Strings.Folders_Count_Singular
                : Strings.Folders_Count_Plural, page.FolderCount));
        if (page.SongCount > 0)
            parts.Add(ResourceFormatter.Format(page.SongCount == 1
                ? Strings.Songs_Count_Singular
                : Strings.Songs_Count_Plural, page.SongCount));

        return parts.Count == 0 ? Strings.Generic_NoItems : string.Join(", ", parts);
    }

    protected override string LoadErrorText => Strings.Folders_ErrorLoading;

    // Play all covers subfolders too, so a folder that holds only subfolders still has something to play.
    protected override bool HasPlayableSongs => TotalItemCount > 0;

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (!_rootFolderId.HasValue || string.IsNullOrEmpty(_currentDirectoryPath))
            return Task.FromResult(new List<Guid>());

        // Searching plays what the page shows: the matching songs of this folder. Otherwise the folder plays with its subfolders.
        SongScope scope = IsSearchActive
            ? new SongScope.Directory(_rootFolderId.Value, _currentDirectoryPath)
            : new SongScope.DirectoryTree(_rootFolderId.Value, _currentDirectoryPath);
        return _libraryReader.GetSongIdsAsync(scope, SearchTerm, sortOrder, token);
    }


    [RelayCommand]
    private Task NavigateToSubfolderAsync(Folder? folder) =>
        folder is null ? Task.CompletedTask : NavigateToDirectoryAsync(folder.Path);

    [RelayCommand]
    private Task NavigateToBreadcrumbAsync(BreadcrumbItem? breadcrumb) =>
        breadcrumb is null || breadcrumb.IsLast ? Task.CompletedTask : NavigateToDirectoryAsync(breadcrumb.Path);

    private async Task NavigateToDirectoryAsync(string path)
    {
        if (_isDisposed || IsLoading || _isNavigating || !_rootFolderId.HasValue) return;

        try
        {
            _isNavigating = true;
            DeselectAll();
            _currentDirectoryPath = path;
            Songs.Clear();
            FolderContents.Clear();
            UpdateBreadcrumbs();
            CurrentPage = 1;
            await LoadAsync(CancellationToken.None);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();
        IsAtRootLevel = string.IsNullOrEmpty(_currentDirectoryPath) || string.IsNullOrEmpty(_rootFolderPath) ||
            string.Equals(Path.TrimEndingDirectorySeparator(_currentDirectoryPath),
                Path.TrimEndingDirectorySeparator(_rootFolderPath), StringComparison.OrdinalIgnoreCase);

        if (!_rootFolderId.HasValue || string.IsNullOrEmpty(_rootFolderPath)) return;

        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(_rootFolderPath));
        Breadcrumbs.Add(new BreadcrumbItem
        {
            Name = string.IsNullOrEmpty(rootName) ? _rootFolderPath : rootName,
            Path = _rootFolderPath
        });

        if (!string.IsNullOrEmpty(_currentDirectoryPath))
        {
            var relativePath = Path.GetRelativePath(_rootFolderPath, _currentDirectoryPath);
            if (relativePath != ".")
            {
                var currentPath = _rootFolderPath;
                foreach (var part in relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    currentPath = Path.Combine(currentPath, part);
                    Breadcrumbs.Add(new BreadcrumbItem { Name = part, Path = currentPath });
                }
            }
        }

        Breadcrumbs[^1].IsLast = true;
    }

    protected override PlaybackContext GetPlaybackContext() =>
        _rootFolderId.HasValue ? new(PlaybackContextType.Folder, _rootFolderId.Value) : base.GetPlaybackContext();

    [RelayCommand]
    private Task PlaySubfolderAsync(Folder? folder)
    {
        if (_isDisposed || folder == null || _rootFolderId is not { } rootFolderId) return Task.CompletedTask;
        var generation = CaptureSongContext();
        return RunActionAsync(async () =>
        {
            var songIds = await _libraryReader.GetSongIdsAsync(
                new SongScope.DirectoryTree(rootFolderId, folder.Path), null, SongSortOrder.TitleAsc, CancellationToken.None);

            if (IsSongContextCurrent(generation) && songIds.Count > 0)
                await _playbackService.PlayAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Folder, folder.Id));
        });
    }

    public override int SelectedItemsCount => base.SelectedItemsCount + _selectedFolders.Count;

    public override void OnSongsSelectionChanged(IEnumerable<object> selectedItems)
    {
        // Rows are subfolders or songs; selected subfolders contribute all the songs inside them.
        var rows = selectedItems.OfType<FolderContentItem>().ToList();
        _selectedFolders.Clear();
        _selectedFolders.AddRange(rows.Select(row => row.Folder).OfType<Folder>());
        base.OnSongsSelectionChanged(rows.Select(row => row.Song).OfType<Song>());
    }

    public override void DeselectAll()
    {
        _selectedFolders.Clear();
        base.DeselectAll();
    }

    protected override async Task<List<Guid>> GetCurrentSelectionIdsAsync()
    {
        if (_isDisposed || !_rootFolderId.HasValue) return [];
        var rootFolderId = _rootFolderId.Value;
        var generation = CaptureSongContext();
        var folderPaths = _selectedFolders.Select(folder => folder.Path).ToList();
        var ids = await base.GetCurrentSelectionIdsAsync();

        foreach (var folderPath in folderPaths)
        {
            try
            {
                var folderSongs = await _libraryReader.GetSongIdsAsync(
                    new SongScope.DirectoryTree(rootFolderId, folderPath), null, SongSortOrder.TitleAsc, CancellationToken.None);
                ids.AddRange(folderSongs.Except(ids));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve songs for selected folder {FolderPath}", folderPath);
            }
            if (!IsSongContextCurrent(generation)) return [];
        }

        return ids;
    }

    public override void ResetState()
    {
        base.ResetState();
        FolderContents.Clear();
        Breadcrumbs.Clear();
        _logger.LogDebug("Cleaned up resources for folder {FolderId}", _rootFolderId);
    }
}
