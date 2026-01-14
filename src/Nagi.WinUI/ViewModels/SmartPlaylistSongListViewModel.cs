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
    private readonly ISmartPlaylistService _smartPlaylistService;
    private Guid? _currentSmartPlaylistId;
    private SmartPlaylist? _currentSmartPlaylist;

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




    /// <summary>
    ///     Gets the current smart playlist ID for editing purposes.
    /// </summary>
    public Guid? GetSmartPlaylistId() => _currentSmartPlaylistId;

    /// <summary>
    ///     Gets the rule summary text for display in the UI.
    /// </summary>
    [ObservableProperty]
    public partial string RuleSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? CoverImageUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverImageUri);



    /// <summary>
    ///     Initializes the view model for a specific smart playlist.
    /// </summary>
    public async Task InitializeAsync(string title, Guid? smartPlaylistId, string? coverImageUri = null)
    {
        if (IsOverallLoading) return;
        _logger.LogDebug("Initializing for smart playlist '{Title}' (ID: {SmartPlaylistId})", title, smartPlaylistId);

        try
        {
            PageTitle = title;
            _currentSmartPlaylistId = smartPlaylistId;
            CoverImageUri = coverImageUri;

            if (smartPlaylistId.HasValue)
            {
                _currentSmartPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(smartPlaylistId.Value).ConfigureAwait(true);
                if (_currentSmartPlaylist != null)
                {
                    RuleSummary = BuildRuleSummary(_currentSmartPlaylist);
                }
            }

            await RefreshOrSortSongsCommand.ExecuteAsync(null).ConfigureAwait(true);
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
    ///     Cleans up resources specific to this view model.
    /// </summary>
    public override void Cleanup()
    {
        _logger.LogDebug("Cleaning up SmartPlaylistSongListViewModel resources");

        _currentSmartPlaylist = null;
        _currentSmartPlaylistId = null;

        base.Cleanup();
    }
}
