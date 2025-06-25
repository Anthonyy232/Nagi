// Nagi/ViewModels/LibraryViewModel.cs

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nagi.Models;
using Nagi.Services;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
///     ViewModel for the main library page, responsible for displaying all songs
///     and initiating library scans.
/// </summary>
public partial class LibraryViewModel : SongListViewModelBase
{
    //
    // This flag ensures the background refresh is only triggered once per application session.
    //
    private static bool _isInitialScanTriggered;

    public LibraryViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService)
    {
    }

    /// <summary>
    ///     The data from GetAllSongsAsync is already sorted by the database query,
    ///     so we prevent the base class from performing a redundant in-memory sort.
    /// </summary>
    protected override bool IsDataPreSortedAfterLoad => true;

    /// <summary>
    ///     Loads all songs from the library, sorted according to the current user preference.
    /// </summary>
    protected override async Task<IEnumerable<Song>> LoadSongsAsync()
    {
        return await _libraryService.GetAllSongsAsync(CurrentSortOrder);
    }

    /// <summary>
    ///     Performs the initial load of songs for the UI and starts a background task
    ///     to scan for any changes in the library folders.
    /// </summary>
    public async Task InitializeAndStartBackgroundScanAsync()
    {
        if (RefreshOrSortSongsCommand.CanExecute(null)) await RefreshOrSortSongsCommand.ExecuteAsync(null);

        if (_isInitialScanTriggered) return;
        _isInitialScanTriggered = true;

        _ = Task.Run(async () =>
        {
            Debug.WriteLine("[LibraryViewModel] Starting initial background library refresh...");
            var libraryService = App.Services.GetRequiredService<ILibraryService>();
            var changesFound = await libraryService.RefreshAllFoldersAsync();
            if (changesFound)
            {
                Debug.WriteLine(
                    "[LibraryViewModel] Background refresh found changes. Reloading song list on UI thread.");
                await RefreshOrSortSongsCommand.ExecuteAsync(null);
            }
            else
            {
                Debug.WriteLine("[LibraryViewModel] Background refresh complete. No changes were found.");
            }
        });
    }
}