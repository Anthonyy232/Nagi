using System;
using System.Collections.Generic;
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
    private Guid _genreId;

    public GenreViewViewModel(
        ILibraryService libraryService,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<GenreViewViewModel> logger)
        : base(libraryService, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
    {
        GenreName = Nagi.WinUI.Resources.Strings.GenreView_DefaultName;

        CurrentSortOrder = SongSortOrder.TitleAsc;
    }

    [ObservableProperty] public partial string GenreName { get; set; }


    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (_genreId == Guid.Empty) return Task.FromResult(new PagedResult<Song>());

        if (IsSearchActive)
            return _libraryReader.SearchSongsInGenrePagedAsync(_genreId, SearchTerm, pageNumber, pageSize, cancellationToken);

        return _libraryReader.GetSongsByGenreIdPagedAsync(_genreId, pageNumber, pageSize, sortOrder, cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (_genreId == Guid.Empty) return Task.FromResult(new List<Guid>());

        if (IsSearchActive)
            return _libraryReader.SearchAllSongIdsInGenreAsync(_genreId, SearchTerm, sortOrder, token);

        return _libraryReader.GetAllSongIdsByGenreIdAsync(_genreId, sortOrder, token);
    }

    /// <summary>
    ///     Loads the details and songs for a specific genre.
    /// </summary>
    /// <param name="navParam">The navigation parameter containing the genre's ID and name.</param>
    [RelayCommand]
    public async Task LoadGenreDetailsAsync(GenreViewNavigationParameter? navParam)
    {
        if (_isDisposed || navParam is null) return;
        ResetState();

        _logger.LogDebug("Loading details for genre '{GenreName}' ({GenreId})", navParam.GenreName,
            navParam.GenreId);

        try
        {
            _genreId = navParam.GenreId;
            GenreName = navParam.GenreName;
            PageTitle = navParam.GenreName;

            var sortOrder = await _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.GenreViewSortOrderKey);
            CurrentSortOrder = sortOrder;
            await RefreshOrSortSongsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load details for GenreId {GenreId}", navParam.GenreId);
            GenreName = Nagi.WinUI.Resources.Strings.GenreView_Error;
            PageTitle = Nagi.WinUI.Resources.Strings.Generic_Error;
            TotalItemsText = Nagi.WinUI.Resources.Strings.Generic_Error;
            Songs.Clear();
        }
    }


    protected override PlaybackContext GetPlaybackContext() =>
        _genreId != Guid.Empty ? new(PlaybackContextType.Genre, _genreId) : base.GetPlaybackContext();

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.GenreViewSortOrderKey, sortOrder);
    }

}
