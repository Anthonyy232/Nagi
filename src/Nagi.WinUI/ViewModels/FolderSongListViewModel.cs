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

/// <summary>
///     Represents a breadcrumb navigation item for folder hierarchy.
/// </summary>
public partial class BreadcrumbItem : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Path { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsLast { get; set; }
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

    // Cached per navigation — both are invalidated by ResolveCurrentParentFolderIdAsync
    private Guid? _currentParentFolderId;
    private int _totalFolderCount;

    private bool _isNavigating;

    public FolderSongListViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<FolderSongListViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
    {
    }

    [ObservableProperty]
    public partial ObservableRangeCollection<FolderContentItem> FolderContents { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<BreadcrumbItem> Breadcrumbs { get; set; } = new();

    [ObservableProperty]
    public partial bool IsAtRootLevel { get; set; } = true;

    public override int SelectedItemsCount
    {
        get
        {
            try
            {
                _stateLock.EnterReadLock();
                try
                {
                    return SelectionState.GetSelectedCount(_fullSongIdList.Count + _totalFolderCount);
                }
                finally
                {
                    _stateLock.ExitReadLock();
                }
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    public async Task InitializeAsync(string title, Guid? rootFolderId, string? directoryPath = null)
    {
        if (IsOverallLoading) return;

        try
        {
            _rootFolderId = rootFolderId;
            _currentDirectoryPath = directoryPath ?? string.Empty;

            if (_rootFolderId.HasValue)
            {
                var rootFolderTask = _libraryReader.GetFolderByIdAsync(_rootFolderId.Value);
                var sortTask = _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.FolderViewSortOrderKey);

                await Task.WhenAll(rootFolderTask, sortTask);

                _rootFolderPath = rootFolderTask.Result?.Path;
                CurrentSortOrder = sortTask.Result;

                if (string.IsNullOrEmpty(_currentDirectoryPath))
                    _currentDirectoryPath = _rootFolderPath ?? string.Empty;
            }
            else
            {
                CurrentSortOrder = await _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.FolderViewSortOrderKey);
            }

            PageTitle = title;
            UpdateBreadcrumbs();
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load directory items for {Path}", _currentDirectoryPath);
            TotalItemsText = Nagi.WinUI.Resources.Strings.Folders_ErrorLoading;
            FolderContents.Clear();
            Songs.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // Combined Pagination Core
    // -------------------------------------------------------------------------

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
    ///     Resolves and caches the folder DB ID for the current directory path. Also updates IsAtRootLevel.
    /// </summary>
    private async Task<Guid?> ResolveCurrentParentFolderIdAsync()
    {
        if (!_rootFolderId.HasValue) return null;

        IsAtRootLevel = string.IsNullOrEmpty(_currentDirectoryPath) ||
                        string.Equals(_currentDirectoryPath, _rootFolderPath, StringComparison.OrdinalIgnoreCase);

        if (IsAtRootLevel)
        {
            _currentParentFolderId = _rootFolderId.Value;
            return _rootFolderId.Value;
        }

        var folder = await _libraryReader.GetFolderByDirectoryPathAsync(_rootFolderId.Value, _currentDirectoryPath)
            .ConfigureAwait(false);
        _currentParentFolderId = folder?.Id;
        return _currentParentFolderId;
    }

    /// <summary>
    ///     Returns the total number of directly-visible subfolders, using the search query when active.
    /// </summary>
    private Task<int> FetchTotalFolderCountAsync(Guid parentFolderId, CancellationToken token = default)
    {
        if (IsSearchActive && !string.IsNullOrWhiteSpace(SearchTerm))
            return _libraryReader.GetSubFolderCountBySearchAsync(parentFolderId, SearchTerm, token);

        return _libraryReader.GetSubFolderCountAsync(parentFolderId, token);
    }

    /// <summary>
    ///     Fetches exactly the folder slice needed for this page. Returns empty when <paramref name="take"/> is 0.
    /// </summary>
    private Task<IEnumerable<Folder>> FetchFolderSliceAsync(Guid parentFolderId, int skip, int take, CancellationToken token = default)
    {
        if (take <= 0) return Task.FromResult(Enumerable.Empty<Folder>());

        if (IsSearchActive && !string.IsNullOrWhiteSpace(SearchTerm))
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

        if (IsSearchActive && !string.IsNullOrWhiteSpace(SearchTerm))
            return _libraryReader.SearchSongsInFolderOffsetAsync(_rootFolderId.Value, _currentDirectoryPath, SearchTerm, skip, take, sortOrder, token);

        return _libraryReader.GetSongsInDirectoryOffsetAsync(
            _rootFolderId.Value, _currentDirectoryPath, skip, take, sortOrder, token);
    }

    /// <summary>
    ///     Loads a single page of combined folder+song content. Both slices are fetched in parallel.
    ///     Dispatches all UI updates atomically.
    /// </summary>
    private async Task LoadCombinedPageAsync(int pageNumber, Guid parentFolderId, int totalFolderCount, CancellationToken token)
    {
        var (folderSkip, foldersToLoad, songSkip, songsToLoad) =
            ComputePageSlices(pageNumber, SongsPerPage, totalFolderCount);

        // Fetch both data slices in parallel — exactly what the page needs, nothing more.
        var folderTask = FetchFolderSliceAsync(parentFolderId, folderSkip, foldersToLoad, token);
        var songTask = FetchSongSliceAsync(songSkip, songsToLoad, CurrentSortOrder, token);

        await Task.WhenAll(folderTask, songTask).ConfigureAwait(false);
        if (token.IsCancellationRequested) return;

        var folders = folderTask.Result.ToList();
        var songResult = songTask.Result;

        // Compute combined pagination meta.
        var combinedTotal = totalFolderCount + songResult.TotalCount;
        var totalPages = SongsPerPage > 0 ? (int)Math.Ceiling(combinedTotal / (double)SongsPerPage) : 1;

        _stateLock.EnterWriteLock();
        try { _totalItemCount = songResult.TotalCount; }
        finally { _stateLock.ExitWriteLock(); }

        // All UI updates in one dispatch to avoid partial-render states.
        _dispatcherService.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested) return;

            Songs.ReplaceRange(songResult.Items);

            var newItems = folders.Select(FolderContentItem.FromFolder)
                .Concat(songResult.Items.Select(FolderContentItem.FromSong));
            FolderContents.ReplaceRange(newItems);

            CurrentPage = pageNumber;
            TotalPages = Math.Max(1, totalPages);
            HasNextPage = pageNumber < TotalPages;
            HasPreviousPage = pageNumber > 1;

            UpdateTotalItemsText(songResult.TotalCount);
            UpdateSelectionStatus();
            PlayAllSongsCommand.NotifyCanExecuteChanged();
            ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
        });
    }

    // -------------------------------------------------------------------------
    // Base class overrides
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Overrides the base refresh to resolve the parent folder, fetch counts and song IDs in parallel,
    ///     then load page 1 using the combined math.
    /// </summary>
    public override async Task RefreshOrSortSongsAsync(string? sortOrderString = null, CancellationToken manualToken = default)
    {
        lock (_loadLock)
        {
            if (IsOverallLoading) return;
            IsOverallLoading = true;
        }

        if (!string.IsNullOrEmpty(sortOrderString) &&
            Enum.TryParse<SongSortOrder>(sortOrderString, true, out var newSortOrder) &&
            newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            CurrentPage = 1;
            _ = SaveSortOrderAsync(newSortOrder)
                .ContinueWith(t => _logger.LogError(t.Exception, "Failed to save sort order"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        UpdateSortOrderButtonText(CurrentSortOrder);

        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();
        _pagedLoadCts = manualToken != default
            ? CancellationTokenSource.CreateLinkedTokenSource(manualToken)
            : new CancellationTokenSource();
        var token = _pagedLoadCts.Token;

        try
        {
            // Resolve and cache parent folder ID (includes IsAtRootLevel update).
            var parentFolderId = await ResolveCurrentParentFolderIdAsync().ConfigureAwait(false);
            if (!parentFolderId.HasValue || token.IsCancellationRequested) return;

            // Parallel: total folder count + all song IDs (for Play All). Both are cheap COUNT/ID queries.
            var countTask = FetchTotalFolderCountAsync(parentFolderId.Value, token);
            var idsTask = LoadAllSongIdsAsync(CurrentSortOrder, token);
            await Task.WhenAll(countTask, idsTask).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;

            _totalFolderCount = countTask.Result;

            _stateLock.EnterWriteLock();
            try { _fullSongIdList = idsTask.Result; }
            finally { _stateLock.ExitWriteLock(); }

            await LoadCombinedPageAsync(1, parentFolderId.Value, _totalFolderCount, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Folder song refresh was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh folder contents for {Path}", _currentDirectoryPath);
            _dispatcherService.TryEnqueue(() => TotalItemsText = Nagi.WinUI.Resources.Strings.Folders_ErrorLoading);
        }
        finally
        {
            _dispatcherService.TryEnqueue(() =>
            {
                IsOverallLoading = false;
                PlayAllSongsCommand.NotifyCanExecuteChanged();
                ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
            });
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
            if (IsOverallLoading || !IsPaginationEnabled) return;
            IsOverallLoading = true;
        }

        _pagedLoadCts?.Cancel();
        _pagedLoadCts?.Dispose();
        _pagedLoadCts = new CancellationTokenSource();
        var token = _pagedLoadCts.Token;

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
        }
        finally
        {
            _dispatcherService.TryEnqueue(() => IsOverallLoading = false);
        }
    }

    // LoadAllSongIdsAsync is still used by the base class for Play All.
    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (!_rootFolderId.HasValue || string.IsNullOrEmpty(_currentDirectoryPath))
            return Task.FromResult(new List<Guid>());

        if (IsSearchActive)
            return _libraryReader.SearchAllSongIdsInFolderAsync(_rootFolderId.Value, SearchTerm, sortOrder, token);

        return _libraryReader.GetAllSongIdsInDirectoryRecursiveAsync(_rootFolderId.Value, _currentDirectoryPath, sortOrder, token);
    }

    // -------------------------------------------------------------------------
    // Navigation
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task NavigateToSubfolderAsync(Folder folder)
    {
        if (IsOverallLoading || _isNavigating) return;
        if (folder == null || !_rootFolderId.HasValue) return;

        try
        {
            _isNavigating = true;
            DeselectAll();
            _currentDirectoryPath = folder.Path;

            Songs.Clear();
            FolderContents.Clear();
            UpdateBreadcrumbs();

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to subfolder {FolderPath}", folder.Path);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    [RelayCommand]
    private async Task NavigateToBreadcrumbAsync(BreadcrumbItem breadcrumb)
    {
        if (IsOverallLoading || _isNavigating) return;
        if (breadcrumb == null || breadcrumb.IsLast) return;

        try
        {
            _isNavigating = true;
            DeselectAll();
            _currentDirectoryPath = string.IsNullOrEmpty(breadcrumb.Path) ||
                                    string.Equals(breadcrumb.Path, _rootFolderPath, StringComparison.OrdinalIgnoreCase)
                ? _rootFolderPath ?? string.Empty
                : breadcrumb.Path;

            Songs.Clear();
            FolderContents.Clear();
            UpdateBreadcrumbs();

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to breadcrumb path {Path}", breadcrumb.Path);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    [RelayCommand]
    private async Task NavigateUpAsync()
    {
        if (IsOverallLoading || _isNavigating) return;
        if (!_rootFolderId.HasValue || IsAtRootLevel) return;

        try
        {
            _isNavigating = true;
            DeselectAll();
            if (string.IsNullOrEmpty(_currentDirectoryPath)) return;

            var normalizedCurrent = _currentDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parentPath = Path.GetDirectoryName(normalizedCurrent);

            _currentDirectoryPath = string.IsNullOrEmpty(parentPath) ||
                                    string.Equals(parentPath, _rootFolderPath, StringComparison.OrdinalIgnoreCase)
                ? _rootFolderPath ?? string.Empty
                : parentPath;

            Songs.Clear();
            FolderContents.Clear();
            UpdateBreadcrumbs();

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate up from {CurrentPath}", _currentDirectoryPath);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();

        if (!_rootFolderId.HasValue || string.IsNullOrEmpty(_rootFolderPath)) return;

        var rootName = Path.GetFileName(_rootFolderPath) ?? PageTitle;
        Breadcrumbs.Add(new BreadcrumbItem
        {
            Name = rootName,
            Path = _rootFolderPath,
            IsLast = string.IsNullOrEmpty(_currentDirectoryPath) ||
                     string.Equals(_currentDirectoryPath, _rootFolderPath, StringComparison.OrdinalIgnoreCase)
        });

        if (!string.IsNullOrEmpty(_currentDirectoryPath) &&
            !string.Equals(_currentDirectoryPath, _rootFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = _currentDirectoryPath.Substring(_rootFolderPath.Length).TrimStart('\\', '/');
            var pathParts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            var currentPath = _rootFolderPath;
            for (var i = 0; i < pathParts.Length; i++)
            {
                currentPath = Path.Combine(currentPath, pathParts[i]);
                Breadcrumbs.Add(new BreadcrumbItem
                {
                    Name = pathParts[i],
                    Path = currentPath,
                    IsLast = i == pathParts.Length - 1
                });
            }
        }

        if (Breadcrumbs.Any())
        {
            foreach (var breadcrumb in Breadcrumbs) breadcrumb.IsLast = false;
            Breadcrumbs[^1].IsLast = true;
        }
    }

    // -------------------------------------------------------------------------
    // Playback & Selection
    // -------------------------------------------------------------------------

    protected override PlaybackContext GetPlaybackContext() =>
        _rootFolderId.HasValue ? new(PlaybackContextType.Folder, _rootFolderId.Value) : base.GetPlaybackContext();

    [RelayCommand]
    private async Task PlaySubfolderAsync(Folder? folder)
    {
        if (folder == null || !_rootFolderId.HasValue) return;

        try
        {
            var songIds = await _libraryReader.GetAllSongIdsInDirectoryRecursiveAsync(
                _rootFolderId.Value, folder.Path, SongSortOrder.TitleAsc, CancellationToken.None);

            if (songIds.Any())
                await _playbackService.PlayAsync(songIds, 0, null, new PlaybackContext(PlaybackContextType.Folder, folder.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing subfolder {SubfolderPath}", folder.Path);
        }
    }

    protected override async Task<List<Guid>> GetCurrentSelectionIdsAsync()
    {
        // SelectionState.GetSelectedIds already handles both select-all and explicit-selection modes
        // correctly for songs. We start from the full song ID list (all pages).
        var selectedIds = SelectionState.GetSelectedIds(_fullSongIdList).ToList();
        var resultSet = new HashSet<Guid>(selectedIds);

        // For explicitly selected folders (non-select-all mode), we expand them into their song IDs.
        // In select-all mode we do NOT subtract unselected folders: FolderContents is page-scoped,
        // so folders on other pages would be silently ignored, producing incorrect results.
        if (!SelectionState.IsSelectAllMode)
        {
            var selectedFolders = FolderContents
                .Where(x => x.IsFolder && SelectionState.IsSelected(x.Id))
                .Select(x => x.Folder!)
                .ToList();

            foreach (var folder in selectedFolders)
            {
                try
                {
                    var folderSongs = await _libraryReader.GetAllSongIdsInDirectoryRecursiveAsync(
                        _rootFolderId!.Value, folder.Path, SongSortOrder.TitleAsc, CancellationToken.None);

                    foreach (var id in folderSongs)
                        resultSet.Add(id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to resolve songs for selected folder {FolderPath}", folder.Path);
                }
            }
        }

        return resultSet.ToList();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void UpdateTotalItemsText(int songCount)
    {
        if (_totalFolderCount > 0 && songCount > 0)
        {
            var folderText = _totalFolderCount == 1
                ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Folders_Count_Singular, _totalFolderCount)
                : ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Folders_Count_Plural, _totalFolderCount);
            var songText = songCount == 1
                ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Songs_Count_Singular, songCount)
                : ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Songs_Count_Plural, songCount);
            TotalItemsText = $"{folderText}, {songText}";
        }
        else if (_totalFolderCount > 0)
        {
            TotalItemsText = _totalFolderCount == 1
                ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Folders_Count_Singular, _totalFolderCount)
                : ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Folders_Count_Plural, _totalFolderCount);
        }
        else if (songCount > 0)
        {
            TotalItemsText = songCount == 1
                ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Songs_Count_Singular, songCount)
                : ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.Songs_Count_Plural, songCount);
        }
        else
        {
            TotalItemsText = Nagi.WinUI.Resources.Strings.Generic_NoItems;
        }
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