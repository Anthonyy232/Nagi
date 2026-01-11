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
///     directory.
///     Supports hierarchical navigation through subfolders.
/// </summary>
public partial class FolderSongListViewModel : SongListViewModelBase
{
    private const int SearchDebounceDelay = 400;
    private readonly IUISettingsService _settingsService;
    private string _currentDirectoryPath = string.Empty;
    private CancellationTokenSource? _debounceCts;
    private Guid? _rootFolderId;
    private string? _rootFolderPath;
    private int _currentFolderCount;

    public FolderSongListViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<FolderSongListViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService, logger)
    {
        _settingsService = settingsService;
    }

    [ObservableProperty] public partial string SearchTerm { get; set; } = string.Empty;

    /// <summary>
    ///     Collection of folder content items (folders and songs) to display.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<FolderContentItem> FolderContents { get; set; } = new();

    /// <summary>
    ///     Breadcrumb navigation items showing the current path in the folder hierarchy.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<BreadcrumbItem> Breadcrumbs { get; set; } = new();

    /// <summary>
    ///     Gets whether the user is currently at the root level of the folder.
    /// </summary>
    [ObservableProperty]
    public partial bool IsAtRootLevel { get; set; } = true;

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);
    
    // Always use paging for efficient loading of large folders.
    protected override bool IsPagingSupported => true;

    partial void OnSearchTermChanged(string value)
    {
        TriggerDebouncedSearch();
    }

    /// <summary>
    ///     Initializes the view model with the details of a specific folder and optional directory path.
    /// </summary>
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
                
                await Task.WhenAll(rootFolderTask, sortTask).ConfigureAwait(true);
                
                _rootFolderPath = rootFolderTask.Result?.Path;
                CurrentSortOrder = sortTask.Result;
                
                // Use root folder path if no specific directory is provided.
                if (string.IsNullOrEmpty(_currentDirectoryPath))
                    _currentDirectoryPath = _rootFolderPath ?? string.Empty;
            }
            else
            {
                CurrentSortOrder = await _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.FolderViewSortOrderKey);
            }

            PageTitle = title;
            
            // Load folders first (typically few), then trigger base class song paging.
            await LoadFoldersAsync();
            UpdateBreadcrumbs();
            
            // Use base class to load songs with paging.
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize folder {FolderId} at path {DirectoryPath}", _rootFolderId,
                directoryPath);
            TotalItemsText = "Error loading folder";
            FolderContents.Clear();
            Songs.Clear();
        }
    }

    /// <summary>
    ///     Loads only the subfolders for the current directory.
    /// </summary>
    private async Task LoadFoldersAsync()
    {
        if (!_rootFolderId.HasValue) return;

        IsAtRootLevel = string.IsNullOrEmpty(_currentDirectoryPath) ||
                        string.Equals(_currentDirectoryPath, _rootFolderPath, StringComparison.OrdinalIgnoreCase);

        // Clear all folder content and reset folder count.
        FolderContents.Clear();
        _currentFolderCount = 0;

        IEnumerable<Folder> subfolders;
        if (IsAtRootLevel)
        {
            subfolders = await _libraryReader.GetSubFoldersAsync(_rootFolderId.Value);
        }
        else
        {
            // _currentDirectoryPath is always set to _rootFolderPath or a valid path during initialization.
            var currentFolder = await _libraryReader.GetFolderByDirectoryPathAsync(_rootFolderId.Value, _currentDirectoryPath);
            subfolders = currentFolder != null
                ? await _libraryReader.GetSubFoldersAsync(currentFolder.Id)
                : Enumerable.Empty<Folder>();
        }

        var orderedFolders = subfolders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Id).ToList();
        _currentFolderCount = orderedFolders.Count;
        
        foreach (var folder in orderedFolders)
            FolderContents.Add(FolderContentItem.FromFolder(folder));
    }

    /// <summary>
    ///     Navigates into a subfolder.
    /// </summary>
    [RelayCommand]
    private async Task NavigateToSubfolderAsync(Folder folder)
    {
        if (IsOverallLoading) return;
        if (folder == null || !_rootFolderId.HasValue) return;

        try
        {
            _currentDirectoryPath = folder.Path;
            Songs.Clear();
            
            await LoadFoldersAsync();
            UpdateBreadcrumbs();
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to subfolder {FolderPath}", folder.Path);
        }
    }

    /// <summary>
    ///     Navigates to a specific path via breadcrumb click.
    /// </summary>
    [RelayCommand]
    private async Task NavigateToBreadcrumbAsync(BreadcrumbItem breadcrumb)
    {
        if (IsOverallLoading) return;
        if (breadcrumb == null || breadcrumb.IsLast) return;

        try
        {
            _currentDirectoryPath = string.IsNullOrEmpty(breadcrumb.Path) ||
                                    string.Equals(breadcrumb.Path, _rootFolderPath, StringComparison.OrdinalIgnoreCase)
                ? _rootFolderPath ?? string.Empty
                : breadcrumb.Path;
            
            Songs.Clear();
            
            await LoadFoldersAsync();
            UpdateBreadcrumbs();
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to breadcrumb path {Path}", breadcrumb.Path);
        }
    }

    /// <summary>
    ///     Navigates up one level in the folder hierarchy.
    /// </summary>
    [RelayCommand]
    private async Task NavigateUpAsync()
    {
        if (IsOverallLoading) return;
        if (!_rootFolderId.HasValue || IsAtRootLevel) return;

        try
        {
            if (string.IsNullOrEmpty(_currentDirectoryPath))
                return;

            var normalizedCurrent = _currentDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parentPath = Path.GetDirectoryName(normalizedCurrent);

            if (string.IsNullOrEmpty(parentPath) ||
                string.Equals(parentPath, _rootFolderPath, StringComparison.OrdinalIgnoreCase))
                _currentDirectoryPath = _rootFolderPath ?? string.Empty;
            else
                _currentDirectoryPath = parentPath;

            Songs.Clear();
            
            await LoadFoldersAsync();
            UpdateBreadcrumbs();
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate up from {CurrentPath}", _currentDirectoryPath);
        }
    }

    /// <summary>
    ///     Updates the breadcrumb navigation based on the current directory path.
    /// </summary>
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

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        if (!_rootFolderId.HasValue || string.IsNullOrEmpty(_currentDirectoryPath))
            return Task.FromResult(new PagedResult<Song>());
        
        if (IsSearchActive)
            return _libraryReader.SearchSongsInFolderPagedAsync(_rootFolderId.Value, SearchTerm, pageNumber, pageSize);
        
        return _libraryReader.GetSongsInDirectoryPagedAsync(_rootFolderId.Value, _currentDirectoryPath, pageNumber, pageSize, sortOrder);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        if (!_rootFolderId.HasValue || string.IsNullOrEmpty(_currentDirectoryPath))
            return Task.FromResult(new List<Guid>());
        
        if (IsSearchActive)
            return _libraryReader.SearchAllSongIdsInFolderAsync(_rootFolderId.Value, SearchTerm, sortOrder);
        
        return _libraryReader.GetSongIdsInDirectoryAsync(_rootFolderId.Value, _currentDirectoryPath, sortOrder);
    }

    protected override Task<IEnumerable<Song>> LoadSongsAsync()
    {
        // Not used since IsPagingSupported is always true.
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    /// <summary>
    ///     Processes a page of results, updating both Songs and FolderContents in a single dispatch.
    ///     This override consolidates all UI updates to avoid race conditions from multiple dispatches.
    /// </summary>
    protected override void ProcessPagedResult(PagedResult<Song> pagedResult, CancellationToken token, bool append = false)
    {
        // Guard against cancelled operations before doing any work to prevent stale data from corrupting the view.
        if (token.IsCancellationRequested || pagedResult?.Items == null) return;

        // Update internal paging state (same as base class, but we handle UI updates ourselves).
        lock (_stateLock)
        {
            _hasNextPage = pagedResult.HasNextPage;
            _totalItemCount = pagedResult.TotalCount;
            _currentPage = pagedResult.PageNumber;
        }

        // Consolidate ALL UI updates into a single dispatch to avoid race conditions.
        _dispatcherService.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested) return;

            if (append)
            {
                // Append new songs to both collections.
                foreach (var song in pagedResult.Items)
                {
                    Songs.Add(song);
                    FolderContents.Add(FolderContentItem.FromSong(song));
                }
            }
            else
            {
                // First page: replace Songs and clear/repopulate song items in FolderContents.
                Songs = new ObservableCollection<Song>(pagedResult.Items);
                ClearSongItemsFromFolderContents();
                foreach (var song in pagedResult.Items)
                    FolderContents.Add(FolderContentItem.FromSong(song));
            }

            // Update TotalItemsText to include folder count.
            UpdateTotalItemsText(pagedResult.TotalCount);
            PlayAllSongsCommand.NotifyCanExecuteChanged();
            ShuffleAndPlayAllSongsCommand.NotifyCanExecuteChanged();
        });
    }
    
    /// <summary>
    ///     Clears only the song items from FolderContents, keeping folder items.
    ///     Uses filter-and-replace for fewer UI change notifications than individual removals.
    /// </summary>
    private void ClearSongItemsFromFolderContents()
    {
        var foldersOnly = FolderContents.Where(x => !x.IsSong).ToList();
        FolderContents.Clear();
        foreach (var folder in foldersOnly)
            FolderContents.Add(folder);
    }

    /// <summary>
    ///     Updates the TotalItemsText to show folder and song counts.
    /// </summary>
    private void UpdateTotalItemsText(int songCount)
    {
        if (_currentFolderCount > 0 && songCount > 0)
        {
            var folderText = _currentFolderCount == 1 ? "folder" : "folders";
            var songText = songCount == 1 ? "song" : "songs";
            TotalItemsText = $"{_currentFolderCount:N0} {folderText}, {songCount:N0} {songText}";
        }
        else if (_currentFolderCount > 0)
        {
            var folderText = _currentFolderCount == 1 ? "folder" : "folders";
            TotalItemsText = $"{_currentFolderCount:N0} {folderText}";
        }
        else if (songCount > 0)
        {
            var songText = songCount == 1 ? "song" : "songs";
            TotalItemsText = $"{songCount:N0} {songText}";
        }
        else
        {
            TotalItemsText = "No items";
        }
    }

    /// <summary>
    ///     Executes an immediate search or refresh, cancelling any pending debounced search.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        _debounceCts?.Cancel();
        await RefreshOrSortSongsCommand.ExecuteAsync(null);
    }

    public override async Task RefreshOrSortSongsAsync(string? sortOrderString = null)
    {
        if (IsOverallLoading) return;
        
        if (!string.IsNullOrEmpty(sortOrderString) &&
            Enum.TryParse<SongSortOrder>(sortOrderString, true, out var newSortOrder)
            && newSortOrder != CurrentSortOrder)
        {
            CurrentSortOrder = newSortOrder;
            _logger.LogDebug("Folder sort order changed to '{SortOrder}'", CurrentSortOrder);
            _ = SaveSortOrderAsync(newSortOrder)
                .ContinueWith(t => _logger.LogError(t.Exception, "Failed to save folder sort order"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        UpdateSortOrderButtonText(CurrentSortOrder);
        
        // Use base class paging to load songs.
        await base.RefreshOrSortSongsAsync(sortOrderString);
    }

    private void TriggerDebouncedSearch()
    {
        try
        {
            _debounceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("CancellationTokenSource was already disposed during search cancellation");
        }

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, token);

                if (token.IsCancellationRequested) return;

                await _dispatcherService.EnqueueAsync(async () =>
                {
                    if (token.IsCancellationRequested) return;
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                });
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug("Debounced search cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Debounced search failed for folder {FolderId}", _rootFolderId);
            }
        }, token);
    }

    /// <summary>
    ///     Plays all songs in a subfolder recursively, including all its subfolders.
    /// </summary>
    [RelayCommand]
    private async Task PlaySubfolderAsync(Folder? folder)
    {
        if (folder == null || !_rootFolderId.HasValue) return;

        try
        {
            var songIds = await _libraryReader.GetAllSongIdsInDirectoryRecursiveAsync(_rootFolderId.Value, folder.Path, SongSortOrder.TitleAsc);

            if (songIds.Any()) await _playbackService.PlayAsync(songIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing subfolder {SubfolderPath}", folder.Path);
        }
    }

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.FolderViewSortOrderKey, sortOrder);
    }

    public override void Cleanup()
    {
        base.Cleanup();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
        SearchTerm = string.Empty;
        FolderContents.Clear();
        Breadcrumbs.Clear();
        _currentFolderCount = 0;
        _logger.LogDebug("Cleaned up resources for folder {FolderId}", _rootFolderId);
    }
}