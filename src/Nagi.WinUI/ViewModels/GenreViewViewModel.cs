using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Provides data and commands for the genre details page, displaying all songs within a specific genre.
/// </summary>
public partial class GenreViewViewModel : SongListViewModelBase
{
    private Guid _genreId;

    public GenreViewViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService)
    {
        CurrentSortOrder = SongSortOrder.TitleAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    [ObservableProperty] public partial string GenreName { get; set; } = "Genre";

    protected override bool IsPagingSupported => true;

    protected override Task<IEnumerable<Song>> LoadSongsAsync()
    {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        if (_genreId == Guid.Empty) return new PagedResult<Song>();

        return await _libraryReader.GetSongsByGenreIdPagedAsync(_genreId, pageNumber, pageSize, sortOrder);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        if (_genreId == Guid.Empty) return new List<Guid>();
        return await _libraryReader.GetAllSongIdsByGenreIdAsync(_genreId, sortOrder);
    }

    /// <summary>
    ///     Loads the details and songs for a specific genre.
    /// </summary>
    /// <param name="navParam">The navigation parameter containing the genre's ID and name.</param>
    [RelayCommand]
    public async Task LoadGenreDetailsAsync(GenreViewNavigationParameter? navParam)
    {
        if (IsOverallLoading || navParam is null) return;

        try
        {
            _genreId = navParam.GenreId;
            GenreName = navParam.GenreName;
            PageTitle = navParam.GenreName;

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[GenreViewViewModel] ERROR: Failed to load details for GenreId: {navParam?.GenreId}. {ex.Message}");
            GenreName = "Error Loading Genre";
            PageTitle = "Error";
            TotalItemsText = "Error";
            Songs.Clear();
        }
    }
}