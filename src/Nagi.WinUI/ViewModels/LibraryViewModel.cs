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
using Nagi.Core.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Manages the main library view, displaying all songs and handling the initial library scan.
/// </summary>
public partial class LibraryViewModel : SongListViewModelBase
{
    private static bool _isInitialScanTriggered;
    private readonly ILibraryScanner _libraryScanner;
    private readonly IUISettingsService _settingsService;

    public LibraryViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        ILibraryScanner libraryScanner,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<LibraryViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService, logger)
    {
        _libraryScanner = libraryScanner;
        _settingsService = settingsService;
        _libraryScanner.ScanCompleted += OnScanCompleted;
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
        var shouldTriggerScan = !_isInitialScanTriggered;
        _isInitialScanTriggered = true;

        CurrentSortOrder = await _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.LibrarySortOrderKey).ConfigureAwait(true);
        await RefreshOrSortSongsCommand.ExecuteAsync(null).ConfigureAwait(true);

        if (!shouldTriggerScan) return;

        _logger.LogDebug("Starting initial background library refresh");
        // We don't await this because we want the UI to be responsive.
        // The ScanCompleted event will trigger a refresh when it finishes.
        _ = _libraryScanner.RefreshAllFoldersAsync();
    }

    private async void OnScanCompleted(object? sender, bool changesFound)
    {
        try
        {
            if (changesFound)
            {
                _logger.LogDebug("Scan completed with changes. Refreshing song list");
                await _dispatcherService.EnqueueAsync(() => RefreshOrSortSongsCommand.ExecuteAsync(null));
            }
            else
            {
                _logger.LogDebug("Scan completed with no changes");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling scan completion in LibraryViewModel");
        }
    }


    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.LibrarySortOrderKey, sortOrder);
    }

    public override void Cleanup()
    {
        base.Cleanup();
        _libraryScanner.ScanCompleted -= OnScanCompleted;
        _logger.LogDebug("Cleaned up LibraryViewModel specific resources");
    }
}