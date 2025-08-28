using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Manages the main library view, displaying all songs and handling the initial library scan.
/// </summary>
public partial class LibraryViewModel : SongListViewModelBase
{
    private const int SearchDebounceDelay = 400;

    private static bool _isInitialScanTriggered;
    private readonly ILibraryScanner _libraryScanner;
    private CancellationTokenSource? _debounceCts;

    public LibraryViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        ILibraryScanner libraryScanner,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger<LibraryViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService, logger)
    {
        _libraryScanner = libraryScanner;
    }

    [ObservableProperty] public partial string SearchTerm { get; set; }

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);
    protected override bool IsPagingSupported => true;

    partial void OnSearchTermChanged(string value)
    {
        TriggerDebouncedSearch();
    }

    protected override Task<IEnumerable<Song>> LoadSongsAsync()
    {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        if (IsSearchActive)
            // When searching, a consistent sort order is applied, ignoring the user's current sort selection.
            return _libraryReader.SearchSongsPagedAsync(SearchTerm, pageNumber, pageSize);
        return _libraryReader.GetAllSongsPagedAsync(pageNumber, pageSize, sortOrder);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        if (IsSearchActive) return await _libraryReader.SearchAllSongIdsAsync(SearchTerm, SongSortOrder.TitleAsc);
        return await _libraryReader.GetAllSongIdsAsync(sortOrder);
    }

    public async Task InitializeAsync()
    {
        await RefreshOrSortSongsCommand.ExecuteAsync(null);

        if (_isInitialScanTriggered) return;
        _isInitialScanTriggered = true;

        // Perform the initial, potentially long-running, library scan on a background thread.
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Starting initial background library refresh");
                var changesFound = await _libraryScanner.RefreshAllFoldersAsync();
                if (changesFound)
                {
                    _logger.LogInformation("Background refresh found changes. Reloading song list");
                    await _dispatcherService.EnqueueAsync(() => RefreshOrSortSongsCommand.ExecuteAsync(null));
                }
                else
                {
                    _logger.LogInformation("Background refresh complete. No changes were found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background library refresh failed");
            }
        });
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
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                });
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug("Debounced search cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Debounced search failed");
            }
        }, token);
    }

    public override void Cleanup()
    {
        base.Cleanup();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        SearchTerm = string.Empty;
        _logger.LogDebug("Cleaned up LibraryViewModel specific resources");
    }
}