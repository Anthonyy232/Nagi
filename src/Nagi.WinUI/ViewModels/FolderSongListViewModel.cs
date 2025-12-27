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
    private string? _currentDirectoryPath;
    private CancellationTokenSource? _debounceCts;
    private Guid? _rootFolderId;
    private string? _rootFolderPath;

    public FolderSongListViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger<FolderSongListViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService, logger)
    {
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
    protected override bool IsPagingSupported => false; // Disable paging for folder view

    partial void OnSearchTermChanged(string value)
    {
        // When the user types in the search box, trigger a debounced search.
        TriggerDebouncedSearch();
    }

    /// <summary>
    ///     Initializes the view model with the details of a specific folder and optional directory path.
    /// </summary>
    /// <param name="title">The title of the folder to display.</param>
    /// <param name="rootFolderId">The unique identifier of the root folder.</param>
    /// <param name="directoryPath">Optional: The directory path within the folder to navigate to.</param>
    public async Task InitializeAsync(string title, Guid? rootFolderId, string? directoryPath = null)
    {
        if (IsOverallLoading) return;

        try
        {
            _rootFolderId = rootFolderId;
            _currentDirectoryPath = directoryPath;

            if (_rootFolderId.HasValue)
            {
                var rootFolder = await _libraryReader.GetFolderByIdAsync(_rootFolderId.Value);
                _rootFolderPath = rootFolder?.Path;
            }

            PageTitle = title;
            await LoadFolderContentsAsync();
            UpdateBreadcrumbs();
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
            await LoadFolderContentsAsync();
            UpdateBreadcrumbs();
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
                ? null
                : breadcrumb.Path;
            await LoadFolderContentsAsync();
            UpdateBreadcrumbs();
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
                // Already at root
                return;

            // Normalize path to handle potential trailing slashes which cause Path.GetDirectoryName 
            // to return the same directory instead of the parent.
            var normalizedCurrent = _currentDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            
            // Navigate to parent directory
            var parentPath = Path.GetDirectoryName(normalizedCurrent);

            // If parent is the root folder path or null, go to root level
            if (string.IsNullOrEmpty(parentPath) ||
                string.Equals(parentPath, _rootFolderPath, StringComparison.OrdinalIgnoreCase))
                _currentDirectoryPath = null;
            else
                _currentDirectoryPath = parentPath;

            await LoadFolderContentsAsync();
            UpdateBreadcrumbs();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate up from {CurrentPath}", _currentDirectoryPath);
        }
    }

    /// <summary>
    ///     Loads the contents (subfolders and songs) of the current directory.
    /// </summary>
    private async Task LoadFolderContentsAsync()
    {
        if (!_rootFolderId.HasValue)
        {
            FolderContents.Clear();
            Songs.Clear();
            return;
        }

        try
        {
            IsOverallLoading = true;
            FolderContents.Clear();
            Songs.Clear();

            var effectivePath = _currentDirectoryPath ?? _rootFolderPath ?? string.Empty;
            IsAtRootLevel = string.IsNullOrEmpty(_currentDirectoryPath) ||
                            string.Equals(_currentDirectoryPath, _rootFolderPath, StringComparison.OrdinalIgnoreCase);

            IEnumerable<Folder> subfolders;
            if (IsAtRootLevel)
            {
                subfolders = await _libraryReader.GetSubFoldersAsync(_rootFolderId.Value);
            }
            else
            {
                var currentFolder =
                    await _libraryReader.GetFolderByDirectoryPathAsync(_rootFolderId.Value, effectivePath);
                if (currentFolder != null)
                    subfolders = await _libraryReader.GetSubFoldersAsync(currentFolder.Id);
                else
                    subfolders = Enumerable.Empty<Folder>();
            }

            IEnumerable<Song> songs;
            if (IsSearchActive)
                songs = await _libraryReader.SearchSongsInFolderAsync(_rootFolderId.Value, SearchTerm);
            else
                songs = await _libraryReader.GetSongsInDirectoryAsync(_rootFolderId.Value, effectivePath);

            foreach (var folder in subfolders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                FolderContents.Add(FolderContentItem.FromFolder(folder));

            var sortedSongs = SortSongs(songs, CurrentSortOrder);
            foreach (var song in sortedSongs)
            {
                FolderContents.Add(FolderContentItem.FromSong(song));
                Songs.Add(song);
            }

            var folderCount = subfolders.Count();
            var songCount = songs.Count();

            if (folderCount > 0 && songCount > 0)
            {
                var folderText = folderCount == 1 ? "folder" : "folders";
                var songText = songCount == 1 ? "song" : "songs";
                TotalItemsText = $"{folderCount:N0} {folderText}, {songCount:N0} {songText}";
            }
            else if (folderCount > 0)
            {
                var folderText = folderCount == 1 ? "folder" : "folders";
                TotalItemsText = $"{folderCount:N0} {folderText}";
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load folder contents for {FolderId} at {DirectoryPath}", _rootFolderId,
                _currentDirectoryPath);
            TotalItemsText = "Error loading contents";
        }
        finally
        {
            IsOverallLoading = false;
        }
    }

    /// <summary>
    ///     Updates the breadcrumb navigation based on the current directory path.
    /// </summary>
    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();

        if (!_rootFolderId.HasValue || string.IsNullOrEmpty(_rootFolderPath)) return;

        // Add root breadcrumb
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
        return Task.FromResult(new PagedResult<Song>());
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        return Task.FromResult(Songs.Select(s => s.Id).ToList());
    }

    protected override Task<IEnumerable<Song>> LoadSongsAsync()
    {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    /// <summary>
    ///     Executes an immediate search or refresh, cancelling any pending debounced search.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        _debounceCts?.Cancel();
        await LoadFolderContentsAsync();
    }

    public override async Task RefreshOrSortSongsAsync(string? sortOrderString = null)
    {
        if (IsOverallLoading) return;

        if (!string.IsNullOrEmpty(sortOrderString) &&
            Enum.TryParse<SongSortOrder>(sortOrderString, true, out var newSortOrder))
        {
            CurrentSortOrder = newSortOrder;
            _logger.LogDebug("Folder sort order changed to '{SortOrder}'", CurrentSortOrder);
        }

        UpdateSortOrderButtonText(CurrentSortOrder);
        await LoadFolderContentsAsync();
    }

    private void TriggerDebouncedSearch()
    {
        try
        {
            _debounceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
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
                    await LoadFolderContentsAsync();
                });
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug("Debounced search cancelled.");
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
            var songs = await _libraryReader.GetSongsInDirectoryRecursiveAsync(_rootFolderId.Value, folder.Path);
            var songList = songs.ToList();

            if (songList.Any()) await _playbackService.PlayAsync(songList);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing subfolder {SubfolderPath}", folder.Path);
        }
    }

    public override void Cleanup()
    {
        base.Cleanup();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        SearchTerm = string.Empty;
        FolderContents.Clear();
        Breadcrumbs.Clear();
        _logger.LogDebug("Cleaned up resources for folder {FolderId}", _rootFolderId);
    }
}