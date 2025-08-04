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
///     Provides data and commands for displaying the songs within a specific library folder.
/// </summary>
public partial class FolderSongListViewModel : SongListViewModelBase
{
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

    protected override bool IsPagingSupported => true;

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

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        if (!_folderId.HasValue) return new PagedResult<Song>();

        return await _libraryReader.GetSongsByFolderIdPagedAsync(_folderId.Value, pageNumber, pageSize, sortOrder);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        if (!_folderId.HasValue) return new List<Guid>();
        return await _libraryReader.GetAllSongIdsByFolderIdAsync(_folderId.Value, sortOrder);
    }

    protected override Task<IEnumerable<Song>> LoadSongsAsync()
    {
        return Task.FromResult(Enumerable.Empty<Song>());
    }
}