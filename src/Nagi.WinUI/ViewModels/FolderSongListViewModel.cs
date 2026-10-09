using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Services.Abstractions;
using Nagi.Core.Helpers;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.ViewModels;

public sealed class BreadcrumbItem
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool IsLast { get; set; }
}

/// <summary>
///     Provides data and commands for displaying the contents (folders and songs) within a specific library folder or
///     directory. Folders and songs are paged together in a single virtual list: folders appear first, then songs.
///     Supports hierarchical navigation through subfolders and folder-name search.
/// </summary>
public partial class FolderSongListViewModel : SongListViewModelBase
{
    private string _currentDirectoryPath = string.Empty;
    private Guid? _rootFolderId;
    private string? _rootFolderPath;

    // Reused by page navigation until the directory is refreshed.
    private Guid? _currentParentFolderId;
    private int _totalFolderCount;

    private bool _isNavigating;

    public FolderSongListViewModel(
        ILibraryService libraryService,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<FolderSongListViewModel> logger)
        : base(libraryService, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
    {
    }

    public ObservableRangeCollection<FolderContentItem> FolderContents { get; } = new();

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();

    [ObservableProperty]
    public partial bool IsAtRootLevel { get; set; } = true;

    public async Task InitializeAsync(string title, Guid? rootFolderId, string? directoryPath = null)
    {
        if (_isDisposed) return;
        ResetState();
        var generation = CaptureSongContext();

        try
        {
            _rootFolderId = rootFolderId;
            _rootFolderPath = null;
            _currentDirectoryPath = directoryPath ?? string.Empty;

            if (_rootFolderId.HasValue)
            {
                var rootFolderTask = _libraryReader.GetFolderByIdAsync(_rootFolderId.Value);
                var sortTask = _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.FolderViewSortOrderKey);

                await Task.WhenAll(rootFolderTask, sortTask);
                if (!IsSongContextCurrent(generation)) return;

                _rootFolderPath = rootFolderTask.Result?.Path;
                CurrentSortOrder = sortTask.Result;

                if (string.IsNullOrEmpty(_currentDirectoryPath))
                    _currentDirectoryPath = _rootFolderPath ?? string.Empty;
            }
            else
            {
                var sortOrder = await _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.FolderViewSortOrderKey);
                if (!IsSongContextCurrent(generation)) return;
                CurrentSortOrder = sortOrder;
            }

            PageTitle = title;
            UpdateBreadcrumbs();
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            if (!IsSongContextCurrent(generation)) return;
            _logger.LogError(ex, "Failed to load directory items for {Path}", _currentDirectoryPath);
            TotalItemsText = Nagi.WinUI.Resources.Strings.Folders_ErrorLoading;
            FolderContents.Clear();
            Songs.Clear();
        }
    }

    /// <summary>
    ///     Computes exactly how many folders and songs belong on a given page.
    ///     Callers request only what they need — no rounding up or over-fetching.
    /// </summary>
    private static (int folderSkip, int foldersToLoad, int songSkip, int songsToLoad)
        ComputePageSlices(int pageNumber, int pageSize, int totalFolders)
    {
        var virtualStart = (pageNumber - 1) * pageSize;
        var foldersToLoad = Math.Max(0, Math.Min(pageSize, totalFolders - virtualStart));
        var folderSkip = virtualStart;
        var songSkip = Math.Max(0, virtualStart - totalFolders);
        var songsToLoad = pageSize - foldersToLoad;
        return (folderSkip, foldersToLoad, songSkip, songsToLoad);
    }

    /// <summary>
    ///     Resolves the folder DB ID for the current directory path.
    /// </summary>
    private async Task<Guid?> ResolveCurrentParentFolderIdAsync(CancellationToken token)
    {
        if (!_rootFolderId.HasValue) return null;

        if (IsAtRootLevel)
            return _rootFolderId.Value;

        var folder = await _libraryReader.GetFolderByPathAsync(_currentDirectoryPath)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return folder?.Id;
    }

    /// <summary>
    ///     Returns the total number of directly-visible subfolders, using the search query when active.
    /// </summary>
    private Task<int> FetchTotalFolderCountAsync(Guid parentFolderId, CancellationToken token = default)
    {
        if (IsSearchActive)
            return _libraryReader.GetSubFolderCountBySearchAsync(parentFolderId, SearchTerm, token);

        return _libraryReader.GetSubFolderCountAsync(parentFolderId, token);
    }

    /// <summary>
    ///     Fetches exactly the folder slice needed for this page. Returns empty when <paramref name="take"/> is 0.
    /// </summary>
    private Task<IEnumerable<Folder>> FetchFolderSliceAsync(Guid parentFolderId, int skip, int take, CancellationToken token = default)
    {
        if (take <= 0) return Task.FromResult(Enumerable.Empty<Folder>());

        if (IsSearchActive)
            return _libraryReader.SearchSubFoldersPagedAsync(parentFolderId, SearchTerm, skip, take, token);

        return _libraryReader.GetSubFoldersPagedAsync(parentFolderId, skip, take, token);
    }

    /// <summary>
    ///     Fetches exactly the song slice needed for this page. Always returns TotalCount even when take=0,
    ///     so the caller can compute total pages without a separate COUNT query.
    /// </summary>
    private Task<PagedResult<Song>> FetchSongSliceAsync(int skip, int take, SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (!_rootFolderId.HasValue || string.IsNullOrEmpty(_currentDirectoryPath))
            return Task.FromResult(new PagedResult<Song>());

        if (IsSearchActive)
            return _libraryReader.SearchSongsInFolderOffsetAsync(_rootFolderId.Value, _currentDirectoryPath, SearchTerm, skip, take, sortOrder, token);

        return _libraryReader.GetSongsInDirectoryOffsetAsync(
            _rootFolderId.Value, _currentDirectoryPath, skip, take, sortOrder, token);
    }

    /// <summary>
    ///     Loads a single page of combined folder+song content. Both slices are fetched in parallel.
    ///     Dispatches all UI updates atomically.
    /// </summary>
    private async Task<bool> LoadCombinedPageAsync(int pageNumber, Guid parentFolderId, int totalFolderCount,
        CancellationToken token, bool append = false, bool clampPage = true)
    {
        var pageSize = PageSize;
        var (folderSkip, foldersToLoad, songSkip, songsToLoad) =
            ComputePageSlices(pageNumber, pageSize, totalFolderCount);

        var folderTask = FetchFolderSliceAsync(parentFolderId, folderSkip, foldersToLoad, token);
        var songTask = FetchSongSliceAsync(songSkip, songsToLoad, CurrentSortOrder, token);

        await Task.WhenAll(folderTask, songTask).ConfigureAwait(false);
        if (token.IsCancellationRequested) return false;

        var folders = folderTask.Result.ToList();
        var songResult = songTask.Result;

        var combinedTotal = totalFolderCount + songResult.TotalCount;
        var totalPages = (int)Math.Ceiling(combinedTotal / (double)pageSize);
        if (clampPage && !append && combinedTotal > 0 && pageNumber > Math.Max(1, totalPages))
            return await LoadCombinedPageAsync(Math.Max(1, totalPages), parentFolderId, totalFolderCount,
                token, clampPage: false).ConfigureAwait(false);

        // All UI updates in one dispatch to avoid partial-render states.
        await _dispatcherService.EnqueueAsync(() =>
        {
            if (token.IsCancellationRequested || _isDisposed || _pendingReload) return Task.CompletedTask;

            Songs.AppendOrReplace(songResult.Items, append);

            var newItems = folders.Select(FolderContentItem.FromFolder)
                .Concat(songResult.Items.Select(FolderContentItem.FromSong));
            FolderContents.AppendOrReplace(newItems, append);

            var isPaged = IsPaginationEnabled && SongsPerPage != 0;
            TotalPages = isPaged ? Math.Max(1, totalPages) : 1;
            CurrentPage = isPaged ? Math.Clamp(pageNumber, 1, TotalPages) : 1;
            TotalItemCount = combinedTotal;

            UpdateTotalItemsText(totalFolderCount, songResult.TotalCount);
            UpdateSelectionStatus();
            PlayAllSongsCommand.NotifyCanExecuteChanged();
            ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
            return Task.CompletedTask;
        }).WaitAsync(token).ConfigureAwait(false);
        return pageNumber < totalPages;
    }

    private async Task LoadRemainingFolderPagesAsync(Guid parentFolderId, int totalFolderCount, CancellationToken token)
    {
        try
        {
            var page = 2;
            while (!token.IsCancellationRequested)
            {
                var hasMore = await Task.Run(() => LoadCombinedPageAsync(page, parentFolderId,
                    totalFolderCount, token, append: true), token);
                if (!hasMore) break;
                page++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load remaining folder contents");
            _dispatcherService.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested && !_isDisposed) HasLoadError = true;
            });
        }
    }

    /// <summary>Loads page 1 after resolving the current folder and item counts.</summary>
    public override async Task RefreshOrSortSongsAsync(string? sortOrderString = null, CancellationToken manualToken = default)
    {
        lock (_loadLock)
        {
            if (_isDisposed) return;
            // Coalesce: if a refresh is already running, queue a follow-up. The finally block
            // re-runs RefreshAsync once the current one finishes, picking up the new sort/page-size.
            if (IsLoading) { _pendingReload = true; return; }
            IsLoading = true;
        }

        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();
        _pagedLoadCts = CancellationTokenSource.CreateLinkedTokenSource(manualToken);
        var token = _pagedLoadCts.Token;
        HasLoadError = false;

        try
        {
            await EnsureInitialSettingsLoadedAsync();
            token.ThrowIfCancellationRequested();

            if (!string.IsNullOrEmpty(sortOrderString) &&
                Enum.TryParse<SongSortOrder>(sortOrderString, true, out var newSortOrder) &&
                newSortOrder != CurrentSortOrder)
            {
                CurrentSortOrder = newSortOrder;
                CurrentPage = 1;
                try { await SaveSortOrderAsync(newSortOrder); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save sort order"); }
            }
            ClearFullSongIds();

            var parentFolderId = await ResolveCurrentParentFolderIdAsync(token).ConfigureAwait(false);
            if (!parentFolderId.HasValue || token.IsCancellationRequested) return;
            _currentParentFolderId = parentFolderId;

            var totalFolderCount = await FetchTotalFolderCountAsync(parentFolderId.Value, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            _totalFolderCount = totalFolderCount;

            var hasMore = await LoadCombinedPageAsync(1, parentFolderId.Value, _totalFolderCount, token).ConfigureAwait(false);
            if (SongsPerPage == 0 && hasMore)
                _ = LoadRemainingFolderPagesAsync(parentFolderId.Value, _totalFolderCount, token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Folder song refresh was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh folder contents for {Path}", _currentDirectoryPath);
            _dispatcherService.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || _isDisposed) return;
                HasLoadError = true;
                TotalItemsText = Nagi.WinUI.Resources.Strings.Folders_ErrorLoading;
            });
        }
        finally
        {
            EndLoadAndDrainPendingReload();
        }
    }

    /// <summary>
    ///     Overrides the base page navigation to use the combined math with cached state.
    ///     No extra DB round-trips are needed — parentFolderId and totalFolderCount are already cached.
    /// </summary>
    public override async Task LoadPageAsync(int pageNumber)
    {
        lock (_loadLock)
        {
            if (_isDisposed || IsLoading || !IsPaginationEnabled) return;
            IsLoading = true;
        }

        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();
        _pagedLoadCts = new CancellationTokenSource();
        var token = _pagedLoadCts.Token;
        HasLoadError = false;

        try
        {
            if (!_currentParentFolderId.HasValue) return;
            await LoadCombinedPageAsync(pageNumber, _currentParentFolderId.Value, _totalFolderCount, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Page load was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load page {PageNumber}", pageNumber);
            _dispatcherService.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested && !_isDisposed) HasLoadError = true;
            });
        }
        finally
        {
            EndLoadAndDrainPendingReload();
        }
    }

    /// <summary>
    ///     Folder paging uses combined folder+song slicing, so external triggers (page-size
    ///     change, settings push, library content change) must route through the folder's own
    ///     refresh envelope — not the base's <c>LoadAsync</c>, which would call the
    ///     non-overridden <c>LoadSongsPagedAsync</c> and clobber <c>Songs</c> with an empty result.
    /// </summary>
    protected override Task RefreshAsync(CancellationToken cancellationToken)
        => RefreshOrSortSongsAsync(null, cancellationToken);

    // LoadAllSongIdsAsync is still used by the base class for Play All.
    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (!_rootFolderId.HasValue || string.IsNullOrEmpty(_currentDirectoryPath))
            return Task.FromResult(new List<Guid>());

        if (IsSearchActive)
            return _libraryReader.SearchAllSongIdsInFolderAsync(_rootFolderId.Value, _currentDirectoryPath, SearchTerm, sortOrder, token);

        return _libraryReader.GetAllSongIdsInDirectoryRecursiveAsync(_rootFolderId.Value, _currentDirectoryPath, sortOrder, token);
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
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to folder {Path}", path);
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
    private async Task PlaySubfolderAsync(Folder? folder)
    {
        if (_isDisposed || folder == null || !_rootFolderId.HasValue) return;
        var generation = CaptureSongContext();

        try
        {
            var songIds = await _libraryReader.GetAllSongIdsInDirectoryRecursiveAsync(
                _rootFolderId.Value, folder.Path, SongSortOrder.TitleAsc, CancellationToken.None);

            if (IsSongContextCurrent(generation) && songIds.Count > 0)
                await _playbackService.PlayAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Folder, folder.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing subfolder {SubfolderPath}", folder.Path);
        }
    }

    protected override async Task<List<Guid>> GetCurrentSelectionIdsAsync()
    {
        if (_isDisposed || !_rootFolderId.HasValue) return [];
        var rootFolderId = _rootFolderId.Value;
        var snapshot = CaptureSelectionSnapshot();
        var selectedFolders = snapshot.Selection.IsSelectAllMode ? [] : FolderContents
            .Where(x => x.IsFolder && snapshot.Selection.IsSelected(x.Id))
            .Select(x => x.Folder!.Path)
            .ToList();
        var allSongIds = await GetFullSongIdsAsync();
        if (!IsSongContextCurrent(snapshot.Generation)) return [];
        var resultSet = snapshot.Selection.GetSelectedIds(allSongIds).ToHashSet();

        // For explicitly selected folders (non-select-all mode), we expand them into their song IDs.
        // In select-all mode we do NOT subtract unselected folders: FolderContents is page-scoped,
        // so folders on other pages would be silently ignored, producing incorrect results.
        foreach (var folderPath in selectedFolders)
        {
            try
            {
                var folderSongs = await _libraryReader.GetAllSongIdsInDirectoryRecursiveAsync(
                    rootFolderId, folderPath, SongSortOrder.TitleAsc, CancellationToken.None);
                resultSet.UnionWith(folderSongs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve songs for selected folder {FolderPath}", folderPath);
            }
            if (!IsSongContextCurrent(snapshot.Generation)) return [];
        }

        return resultSet.ToList();
    }

    private void UpdateTotalItemsText(int folderCount, int songCount)
    {
        var parts = new List<string>();
        if (folderCount > 0)
            parts.Add(ResourceFormatter.Format(folderCount == 1
                ? Nagi.WinUI.Resources.Strings.Folders_Count_Singular
                : Nagi.WinUI.Resources.Strings.Folders_Count_Plural, folderCount));
        if (songCount > 0)
            parts.Add(ResourceFormatter.Format(songCount == 1
                ? Nagi.WinUI.Resources.Strings.Songs_Count_Singular
                : Nagi.WinUI.Resources.Strings.Songs_Count_Plural, songCount));

        TotalItemsText = parts.Count == 0 ? Nagi.WinUI.Resources.Strings.Generic_NoItems : string.Join(", ", parts);
    }

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.FolderViewSortOrderKey, sortOrder);
    }

    public override void ResetState()
    {
        base.ResetState();
        FolderContents.Clear();
        Breadcrumbs.Clear();
        _currentParentFolderId = null;
        _totalFolderCount = 0;
        _logger.LogDebug("Cleaned up resources for folder {FolderId}", _rootFolderId);
    }
}
