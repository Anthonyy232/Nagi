using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Services.Abstractions;
using Nagi.Core.Helpers;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Provides data and commands for the genre details page, displaying all songs within a specific genre.
/// </summary>
public partial class GenreViewViewModel : SongListViewModelBase
{
    private readonly IUISettingsService _settingsService;
    private Guid _genreId;

    public GenreViewViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<GenreViewViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, musicNavigationService, dispatcherService, uiService, logger)
    {
        _settingsService = settingsService;
        GenreName = "Genre";

        CurrentSortOrder = SongSortOrder.TitleAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    [ObservableProperty] public partial string GenreName { get; set; }


    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        if (_genreId == Guid.Empty) return new PagedResult<Song>();

        if (IsSearchActive)
            return await _libraryReader.SearchSongsInGenrePagedAsync(_genreId, SearchTerm, pageNumber, pageSize);

        return await _libraryReader.GetSongsByGenreIdPagedAsync(_genreId, pageNumber, pageSize, sortOrder);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        if (_genreId == Guid.Empty) return new List<Guid>();

        if (IsSearchActive)
            // Assumes a new method exists in the library reader for scoped searching.
            return await _libraryReader.SearchAllSongIdsInGenreAsync(_genreId, SearchTerm, sortOrder);

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

        _logger.LogDebug("Loading details for genre '{GenreName}' ({GenreId})", navParam.GenreName,
            navParam.GenreId);

        try
        {
            _genreId = navParam.GenreId;
            GenreName = navParam.GenreName;
            PageTitle = navParam.GenreName;

            CurrentSortOrder = await _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.GenreViewSortOrderKey);
            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load details for GenreId {GenreId}", navParam?.GenreId);
            GenreName = "Error Loading Genre";
            PageTitle = "Error";
            TotalItemsText = "Error";
            Songs.Clear();
        }
    }


    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.GenreViewSortOrderKey, sortOrder);
    }

    public override void ResetState()
    {
        base.ResetState();
        _logger.LogDebug("Cleaned up GenreViewViewModel search resources");
    }
}