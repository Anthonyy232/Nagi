using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private bool _isInitialScanTriggered;

    public LibraryViewModel(
        ILibraryService libraryService,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<LibraryViewModel> logger)
        : base(libraryService, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
    {
    }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (IsSearchActive)
            // When searching, a consistent sort order is applied, ignoring the user's current sort selection.
            return _libraryReader.SearchSongsPagedAsync(SearchTerm, pageNumber, pageSize, cancellationToken);
        return _libraryReader.GetAllSongsPagedAsync(pageNumber, pageSize, sortOrder, cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        return IsSearchActive
            ? _libraryReader.SearchAllSongIdsAsync(SearchTerm, SongSortOrder.TitleAsc, token)
            : _libraryReader.GetAllSongIdsAsync(sortOrder, token);
    }

    protected override PlaybackContext GetPlaybackContext() =>
        IsSearchActive ? new(PlaybackContextType.Search, null) : new(PlaybackContextType.Library, null);

    public bool HasInitialized { get; private set; }

    public async Task InitializeAsync()
    {
        _logger.LogDebug("Initializing LibraryViewModel");
        CurrentSortOrder = await _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.LibrarySortOrderKey);
        await RefreshOrSortSongsCommand.ExecuteAsync(null);
        HasInitialized = !HasLoadError;

        if (_isInitialScanTriggered) return;
        _isInitialScanTriggered = true;

        _logger.LogDebug("Starting initial background library refresh");
        // LibraryContentChanged refreshes the view when the background scan finishes.
        _ = Task.Run(async () =>
        {
            try
            {
                // Run the dedup pass before refresh so the UI doesn't briefly show duplicate
                // entries from a prior (pre-canonicalization) run.
                await _libraryService.DeduplicateLibraryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Library deduplication pass failed during startup.");
            }
            try
            {
                await _libraryService.RefreshAllFoldersAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Initial background library refresh failed.");
            }
        });
    }

    protected override bool ShouldReloadOnLibraryChange(LibraryChangeType changeType) =>
        changeType != LibraryChangeType.FolderAdded;

    protected override Task RefreshAsync(CancellationToken cancellationToken) =>
        RefreshOrSortSongsAsync(manualToken: cancellationToken);

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.LibrarySortOrderKey, sortOrder);
    }

}
