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

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Provides data and commands for the genre details page, displaying all songs within a specific genre.
/// </summary>
public partial class GenreViewViewModel : SongListViewModelBase
{
    private const int SearchDebounceDelay = 400;
    private CancellationTokenSource? _debounceCts;
    private Guid _genreId;

    public GenreViewViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger<GenreViewViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService, logger)
    {
        GenreName = "Genre";

        CurrentSortOrder = SongSortOrder.TitleAsc;
        UpdateSortOrderButtonText(CurrentSortOrder);
    }

    [ObservableProperty] public partial string GenreName { get; set; }

    [ObservableProperty] public partial string SearchTerm { get; set; }

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);

    protected override bool IsPagingSupported => true;

    partial void OnSearchTermChanged(string value)
    {
        TriggerDebouncedSearch();
    }


    protected override Task<IEnumerable<Song>> LoadSongsAsync()
    {
        return Task.FromResult(Enumerable.Empty<Song>());
    }

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

    /// <summary>
    ///     Executes an immediate search or refresh, cancelling any pending debounced search.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        _debounceCts?.Cancel();
        await RefreshOrSortSongsCommand.ExecuteAsync(null);
    }

    private void TriggerDebouncedSearch()
    {
        try
        {
            _debounceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore exception if the CancellationTokenSource has already been disposed.
        }

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, token);

                if (token.IsCancellationRequested) return;

                await _dispatcherService.EnqueueAsync(async () =>
                {
                    // Re-check the cancellation token after dispatching to prevent a race condition.
                    if (token.IsCancellationRequested) return;
                    await RefreshOrSortSongsCommand.ExecuteAsync(null);
                });
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug("Debounced search cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Debounced search failed for genre {GenreId}", _genreId);
            }
        }, token);
    }

    public override void Cleanup()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        SearchTerm = string.Empty;

        base.Cleanup();
    }
}