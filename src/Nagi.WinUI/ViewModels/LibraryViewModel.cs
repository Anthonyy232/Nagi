using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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
    // Ensures the initial, potentially long-running, background scan is only triggered once per app session.
    private static bool _isInitialScanTriggered;
    private readonly ILibraryScanner _libraryScanner;

    public LibraryViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        ILibraryScanner libraryScanner,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService)
    {
        _libraryScanner = libraryScanner;
    }

    protected override bool IsPagingSupported => true;

    protected override Task<IEnumerable<Song>> LoadSongsAsync()
    {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        return await _libraryReader.GetAllSongsPagedAsync(pageNumber, pageSize, sortOrder);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        return await _libraryReader.GetAllSongIdsAsync(sortOrder);
    }

    /// <summary>
    ///     Initializes the view by loading songs and triggering a one-time background library refresh.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Immediately load and display songs from the current database state.
        await RefreshOrSortSongsCommand.ExecuteAsync(null);

        if (_isInitialScanTriggered) return;
        _isInitialScanTriggered = true;

        // Fire-and-forget the library refresh on a background thread to not block the UI.
        _ = Task.Run(async () =>
        {
            try
            {
                Debug.WriteLine("[LibraryViewModel] INFO: Starting initial background library refresh.");
                var changesFound = await _libraryScanner.RefreshAllFoldersAsync();
                if (changesFound)
                {
                    Debug.WriteLine("[LibraryViewModel] INFO: Background refresh found changes. Reloading song list.");
                    // If changes were found, refresh the UI to show them.
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                }
                else
                {
                    Debug.WriteLine("[LibraryViewModel] INFO: Background refresh complete. No changes were found.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LibraryViewModel] ERROR: Background library refresh failed. {ex.Message}");
            }
        });
    }
}