using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nagi.Models;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
/// ViewModel for the main library page, responsible for displaying all songs
/// and initiating library scans.
/// </summary>
public partial class LibraryViewModel : SongListViewModelBase {
    private static bool _isInitialScanTriggered;

    public LibraryViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService) {
    }

    /// <summary>
    /// Enables gradual, paged loading for the entire library view.
    /// </summary>
    protected override bool IsPagingSupported => true;

    /// <summary>
    /// This method is not used because <see cref="SongListViewModelBase.IsPagingSupported"/> is true.
    /// </summary>
    protected override Task<IEnumerable<Song>> LoadSongsAsync() {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    /// <summary>
    /// Loads a specific page of songs from the entire library.
    /// </summary>
    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) {
        return await _libraryService.GetAllSongsPagedAsync(pageNumber, pageSize, sortOrder);
    }

    /// <summary>
    /// Loads the complete list of song IDs for the entire library.
    /// </summary>
    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        return await _libraryService.GetAllSongIdsAsync(sortOrder);
    }

    /// <summary>
    /// Performs the initial load of songs for the UI and, if it's the first time,
    /// starts a background task to scan for any changes in the library folders.
    /// </summary>
    public async Task InitializeAsync() {
        await RefreshOrSortSongsCommand.ExecuteAsync(null);

        if (_isInitialScanTriggered) return;
        _isInitialScanTriggered = true;

        _ = Task.Run(async () => {
            try {
                Debug.WriteLine("[LibraryViewModel] Starting initial background library refresh...");
                var changesFound = await _libraryService.RefreshAllFoldersAsync();
                if (changesFound) {
                    Debug.WriteLine("[LibraryViewModel] Background refresh found changes. Reloading song list on UI thread.");
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                }
                else {
                    Debug.WriteLine("[LibraryViewModel] Background refresh complete. No changes were found.");
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ERROR] Background library refresh failed: {ex.Message}");
            }
        });
    }
}