using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nagi.Models;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
/// ViewModel for the FolderSongViewPage, responsible for displaying songs
/// from a specific library folder using incremental loading.
/// </summary>
public partial class FolderSongListViewModel : SongListViewModelBase {
    private Guid? _folderId;

    public FolderSongListViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService) {
    }

    /// <summary>
    /// Indicates that this ViewModel supports paged loading.
    /// </summary>
    protected override bool IsPagingSupported => true;

    /// <summary>
    /// Initializes the ViewModel with the folder's details and loads the first page of songs.
    /// </summary>
    /// <param name="title">The title to display for the page.</param>
    /// <param name="folderId">The unique identifier of the folder.</param>
    public async Task InitializeAsync(string title, Guid? folderId) {
        if (IsOverallLoading) return;

        try {
            PageTitle = title;
            _folderId = folderId;
            await RefreshOrSortSongsAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] Failed to initialize FolderSongListViewModel. {ex.Message}");
            TotalItemsText = "Error loading folder";
            Songs.Clear();
        }
    }

    /// <summary>
    /// Loads a specific page of songs from the folder.
    /// </summary>
    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) {
        if (!_folderId.HasValue) {
            return new PagedResult<Song>();
        }

        return await _libraryService.GetSongsByFolderIdPagedAsync(_folderId.Value, pageNumber, pageSize, sortOrder);
    }

    /// <summary>
    /// Fetches the complete, sorted list of song IDs for this folder.
    /// </summary>
    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        if (!_folderId.HasValue) {
            return new List<Guid>();
        }
        return await _libraryService.GetAllSongIdsByFolderIdAsync(_folderId.Value, sortOrder);
    }

    /// <summary>
    /// This method is not used when IsPagingSupported is true, but must be implemented.
    /// </summary>
    protected override Task<IEnumerable<Song>> LoadSongsAsync() {
        return Task.FromResult(Enumerable.Empty<Song>());
    }
}