using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Models;
using Nagi.Navigation;
using Nagi.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Nagi.ViewModels;

/// <summary>
/// ViewModel for the genre details page, displaying all songs within a specific genre.
/// </summary>
public partial class GenreViewViewModel : SongListViewModelBase {
    private Guid _genreId;

    public GenreViewViewModel(
        ILibraryService libraryService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService)
        : base(libraryService, playbackService, navigationService) {
        CurrentSortOrder = SongSortOrder.TitleAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    [ObservableProperty]
    private string _genreName = "Genre";

    protected override bool IsPagingSupported => true;

    protected override Task<IEnumerable<Song>> LoadSongsAsync() {
        // This method is not used for paged views.
        return Task.FromResult(Enumerable.Empty<Song>());
    }

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize, SongSortOrder sortOrder) {
        if (_genreId == Guid.Empty) {
            return new PagedResult<Song>();
        }

        return await _libraryService.GetSongsByGenreIdPagedAsync(_genreId, pageNumber, pageSize, sortOrder);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder) {
        if (_genreId == Guid.Empty) {
            return new List<Guid>();
        }
        return await _libraryService.GetAllSongIdsByGenreIdAsync(_genreId, sortOrder);
    }

    /// <summary>
    /// Loads the details for a specific genre based on navigation parameters.
    /// </summary>
    [RelayCommand]
    public async Task LoadGenreDetailsAsync(GenreViewNavigationParameter? navParam) {
        if (IsOverallLoading || navParam is null) return;

        try {
            _genreId = navParam.GenreId;
            GenreName = navParam.GenreName;
            PageTitle = navParam.GenreName;

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex) {
            // Log the error and update the UI to inform the user.
            Debug.WriteLine($"[ERROR] Failed to load genre details for GenreId: {navParam?.GenreId}. Exception: {ex}");
            GenreName = "Error Loading Genre";
            PageTitle = "Error";
            TotalItemsText = "Error";
            Songs.Clear();
        }
    }
}