// Nagi/ViewModels/FolderSongListViewModel.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nagi.Models;
using Nagi.Services;
using Nagi.Services.Abstractions;

namespace Nagi.ViewModels;

/// <summary>
///     ViewModel for the FolderSongViewPage, responsible for displaying songs
///     from a specific library folder.
/// </summary>
public partial class FolderSongListViewModel : SongListViewModelBase
{
    private Guid? _folderId;

    public FolderSongListViewModel(ILibraryService libraryService, IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService)
    {
    }

    /// <summary>
    ///     Initializes the ViewModel with the folder's details and loads its songs.
    /// </summary>
    /// <param name="title">The title to display for the page, typically the folder name.</param>
    /// <param name="folderId">The unique identifier of the folder.</param>
    public async Task InitializeAsync(string title, Guid? folderId)
    {
        PageTitle = title;
        _folderId = folderId;
        await RefreshOrSortSongsAsync();
    }

    /// <summary>
    ///     Loads the songs from the specific folder identified by its ID.
    /// </summary>
    protected override async Task<IEnumerable<Song>> LoadSongsAsync()
    {
        if (!_folderId.HasValue) return Enumerable.Empty<Song>();
        return await _libraryService.GetSongsByFolderIdAsync(_folderId.Value);
    }
}