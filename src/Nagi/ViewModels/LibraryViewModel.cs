using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nagi.Models;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
/// Provides data and commands for the main library page, which displays all songs
/// in the user's collection and can initiate library scans.
/// </summary>
public partial class LibraryViewModel : SongListViewModelBase {
    private readonly ILibraryScanner _libraryScanner;
    private static bool _isInitialScanTriggered;

    public LibraryViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        ILibraryScanner libraryScanner,
        IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryReader, playlistService, playbackService, navigationService) {
        _libraryScanner = libraryScanner;
    }

    /// <summary>
    /// Enables paged loading for the main library view to handle large collections efficiently.
    /// </summary>
    protected override bool IsPagingSupported => true;

    /// <summary>
    /// This method is not used because paged loading is enabled.
    /// </summary>
    protected override Task<IEnumerable<Song>> LoadSongsAsync() {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    /// <summary>
    /// Loads a specific page of songs from the entire library.
    /// </summary>
    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) {
        return await _libraryReader.GetAllSongsPagedAsync(pageNumber, pageSize, sortOrder);
    }

    /// <summary>
    /// Loads the IDs of all songs in the library for playback purposes.
    /// </summary>
    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        return await _libraryReader.GetAllSongIdsAsync(sortOrder);
    }

    /// <summary>
    /// Performs the initial load of songs for the UI. On the first run, it also
    /// starts a background task to scan for new or changed files in the library folders.
    /// </summary>
    public async Task InitializeAsync() {
        await RefreshOrSortSongsCommand.ExecuteAsync(null);

        if (_isInitialScanTriggered) return;
        _isInitialScanTriggered = true;

        _ = Task.Run(async () => {
            try {
                Debug.WriteLine("[LibraryViewModel] INFO: Starting initial background library refresh.");
                var changesFound = await _libraryScanner.RefreshAllFoldersAsync();
                if (changesFound) {
                    Debug.WriteLine("[LibraryViewModel] INFO: Background refresh found changes. Reloading song list.");
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                }
                else {
                    Debug.WriteLine("[LibraryViewModel] INFO: Background refresh complete. No changes were found.");
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[LibraryViewModel] ERROR: Background library refresh failed. {ex.Message}");
            }
        });
    }
}