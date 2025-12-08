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
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     A specialized song list view model for displaying songs that match a smart playlist's rules.
///     Unlike regular playlists, smart playlist songs are read-only and dynamically generated.
/// </summary>
public partial class SmartPlaylistSongListViewModel : SongListViewModelBase
{
    private const int SearchDebounceDelay = 400;

    private readonly ISmartPlaylistService _smartPlaylistService;
    private Guid? _currentSmartPlaylistId;
    private SmartPlaylist? _currentSmartPlaylist;
    private CancellationTokenSource? _debounceCts;

    public SmartPlaylistSongListViewModel(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        ISmartPlaylistService smartPlaylistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger<SmartPlaylistSongListViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, dispatcherService, uiService, logger)
    {
        _smartPlaylistService = smartPlaylistService;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPagingSupported))]
    public partial string SearchTerm { get; set; } = string.Empty;

    private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchTerm);

    // Always support paging for smart playlists since they can be large
    protected override bool IsPagingSupported => true;

    // Smart playlist songs are sorted by the smart playlist's sort order
    protected override bool IsDataPreSortedAfterLoad => true;

    /// <summary>
    ///     Gets the current smart playlist ID for editing purposes.
    /// </summary>
    public Guid? GetSmartPlaylistId() => _currentSmartPlaylistId;

    /// <summary>
    ///     Gets the rule summary text for display in the UI.
    /// </summary>
    [ObservableProperty]
    public partial string RuleSummary { get; set; } = string.Empty;


    partial void OnSearchTermChanged(string value)
    {
        TriggerDebouncedSearch();
    }

    /// <summary>
    ///     Initializes the view model for a specific smart playlist.
    /// </summary>
    public async Task InitializeAsync(string title, Guid? smartPlaylistId)
    {
        if (IsOverallLoading) return;
        _logger.LogInformation("Initializing for smart playlist '{Title}' (ID: {SmartPlaylistId})", title, smartPlaylistId);

        try
        {
            PageTitle = title;
            _currentSmartPlaylistId = smartPlaylistId;

            if (smartPlaylistId.HasValue)
            {
                _currentSmartPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(smartPlaylistId.Value);
                if (_currentSmartPlaylist != null)
                {
                    RuleSummary = BuildRuleSummary(_currentSmartPlaylist);
                }
            }

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize smart playlist {SmartPlaylistId}", _currentSmartPlaylistId);
            TotalItemsText = "Error loading smart playlist";
            Songs.Clear();
        }
    }

    private static string BuildRuleSummary(SmartPlaylist smartPlaylist)
    {
        var ruleCount = smartPlaylist.Rules.Count;
        if (ruleCount == 0)
            return "No rules defined • All songs match";

        var matchType = smartPlaylist.MatchAllRules ? "all" : "any";
        var ruleWord = ruleCount == 1 ? "rule" : "rules";
        var summary = $"Match {matchType} of {ruleCount} {ruleWord}";



        return summary;
    }

    protected override async Task<IEnumerable<Song>> LoadSongsAsync()
    {
        if (!_currentSmartPlaylistId.HasValue) return Enumerable.Empty<Song>();
        return await _smartPlaylistService.GetMatchingSongsAsync(_currentSmartPlaylistId.Value);
    }

    protected override async Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder)
    {
        if (!_currentSmartPlaylistId.HasValue) return new PagedResult<Song>();



        // Server-side search now supported
        return await _smartPlaylistService.GetMatchingSongsPagedAsync(
            _currentSmartPlaylistId.Value, pageNumber, pageSize, IsSearchActive ? SearchTerm : null);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder)
    {
        if (!_currentSmartPlaylistId.HasValue) return new List<Guid>();
        return await _smartPlaylistService.GetMatchingSongIdsAsync(_currentSmartPlaylistId.Value);
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
        // Dispose the old CTS to prevent resource leaks
        try
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Ignore exception if the CancellationTokenSource has already been disposed.
        }

        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
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
                _logger.LogError(ex, "Debounced search failed for smart playlist {SmartPlaylistId}", _currentSmartPlaylistId);
            }
        }, token);
    }

    /// <summary>
    ///     Cleans up resources specific to this view model.
    /// </summary>
    public override void Cleanup()
    {
        _logger.LogDebug("Cleaning up SmartPlaylistSongListViewModel resources");

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
        _currentSmartPlaylist = null;
        _currentSmartPlaylistId = null;
        SearchTerm = string.Empty;

        base.Cleanup();
    }
}
