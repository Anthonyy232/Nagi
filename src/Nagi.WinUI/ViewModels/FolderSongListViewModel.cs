using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Provides data and commands for displaying the songs within a specific library folder.
/// </summary>
public partial class FolderSongListViewModel : SongListViewModelBase
{
    private const int SearchDebounceDelay = 400;
    private CancellationTokenSource? _debounceCts;
    private Guid? _folderId;

    public FolderSongListViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService)
    {
    }

    [ObservableProperty] public partial string SearchTerm { get; set; }

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);
    protected override bool IsPagingSupported => true;

    partial void OnSearchTermChanged(string value)
    {
        // When the user types in the search box, trigger a debounced search.
        TriggerDebouncedSearch();
    }

    /// <summary>
    ///     Initializes the view model with the details of a specific folder.
    /// </summary>
    /// <param name="title">The title of the folder to display.</param>
    /// <param name="folderId">The unique identifier of the folder.</param>
    public async Task InitializeAsync(string title, Guid? folderId)
    {
        if (IsOverallLoading) return;

        try
        {
            PageTitle = title;
            _folderId = folderId;
            await RefreshOrSortSongsAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FolderSongListViewModel] ERROR: Failed to initialize. {ex.Message}");
            TotalItemsText = "Error loading folder";
            Songs.Clear();
        }
    }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        if (!_folderId.HasValue) return Task.FromResult(new PagedResult<Song>());

        if (IsSearchActive)
            // When searching, a consistent sort order is applied, ignoring the user's current sort selection.
            return _libraryReader.SearchSongsInFolderPagedAsync(_folderId.Value, SearchTerm, pageNumber, pageSize);

        return _libraryReader.GetSongsByFolderIdPagedAsync(_folderId.Value, pageNumber, pageSize, sortOrder);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        if (!_folderId.HasValue) return Task.FromResult(new List<Guid>());

        if (IsSearchActive)
            // Assumes a corresponding method exists in the library reader to get all song IDs for a search in a folder.
            return _libraryReader.SearchAllSongIdsInFolderAsync(_folderId.Value, SearchTerm, SongSortOrder.TitleAsc);

        return _libraryReader.GetAllSongIdsByFolderIdAsync(_folderId.Value, sortOrder);
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
        await RefreshOrSortSongsAsync();
    }

    private void TriggerDebouncedSearch()
    {
        try
        {
            _debounceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore exception if the CancellationTokenSource has already been disposed.
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
                    // Re-check the cancellation token after dispatching to prevent a race condition.
                    if (token.IsCancellationRequested) return;
                    await RefreshOrSortSongsAsync();
                });
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine("[FolderSongListViewModel] Debounced search cancelled.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FolderSongListViewModel] ERROR: Debounced search failed. {ex.Message}");
            }
        }, token);
    }

    public override void Cleanup()
    {
        base.Cleanup();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        SearchTerm = string.Empty;
        Debug.WriteLine("[FolderSongListViewModel] Cleaned up FolderSongListViewModel specific resources.");
    }
}