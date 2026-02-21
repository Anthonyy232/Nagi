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
    private readonly ILibraryService _libraryService;
    private CancellationTokenSource? _debouncer;

    public LibraryViewModel(
        ILibraryService libraryService,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<LibraryViewModel> logger)
        : base(libraryService, playlistService, playbackService, navigationService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
    {
        _libraryService = libraryService;
        _libraryService.LibraryContentChanged += OnLibraryContentChanged;
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
        // The LibraryContentChanged event will trigger a refresh when it finishes.
        _ = _libraryService.RefreshAllFoldersAsync();
    }

    private void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        // We don't need to refresh the song list just because a folder container was added (it has no songs yet).
        // We wait for the subsequent scan to update us.
        if (e.ChangeType == LibraryChangeType.FolderAdded) return;

        // Debounce to prevent multiple refresh calls during rapid changes.
        var oldCts = Interlocked.Exchange(ref _debouncer, new CancellationTokenSource());
        try { oldCts?.Cancel(); } catch (ObjectDisposedException) { }
        
        var token = _debouncer.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                
                _logger.LogDebug("Library content changed ({ChangeType}). Refreshing song list.", e.ChangeType);
                await _dispatcherService.EnqueueAsync(() => RefreshOrSortSongsCommand.ExecuteAsync(null));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling library content change in LibraryViewModel");
            }
        }, token);
    }


    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.LibrarySortOrderKey, sortOrder);
    }

}