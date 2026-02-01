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
using Nagi.WinUI.Helpers;

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
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger<SmartPlaylistSongListViewModel> logger)
        : base(libraryReader, playlistService, playbackService, navigationService, musicNavigationService, dispatcherService, uiService, logger)
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

            Task<SmartPlaylist?>? playlistTask = null;
            if (smartPlaylistId.HasValue)
            {
                playlistTask = _smartPlaylistService.GetSmartPlaylistByIdAsync(smartPlaylistId.Value);
            }

            // Start songs loading in parallel
            var songsTask = RefreshOrSortSongsCommand.ExecuteAsync(null);

            if (playlistTask != null)
            {
                _currentSmartPlaylist = await playlistTask.ConfigureAwait(true);
                if (_currentSmartPlaylist != null)
                {
                    RuleSummary = BuildRuleSummary(_currentSmartPlaylist);
                }
            }

            await songsTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize smart playlist {SmartPlaylistId}", _currentSmartPlaylistId);
            TotalItemsText = Nagi.WinUI.Resources.Strings.SmartPlaylist_ErrorLoading;
            Songs.Clear();
        }
    }

    private static string BuildRuleSummary(SmartPlaylist smartPlaylist)
    {
        var ruleCount = smartPlaylist.Rules.Count;
        if (ruleCount == 0)
            return Nagi.WinUI.Resources.Strings.SmartPlaylist_RuleSummary_NoRules;

        var matchType = smartPlaylist.MatchAllRules 
            ? Nagi.WinUI.Resources.Strings.SmartPlaylist_MatchAll 
            : Nagi.WinUI.Resources.Strings.SmartPlaylist_MatchAny;
        var ruleWord = ruleCount == 1 
            ? Nagi.WinUI.Resources.Strings.SmartPlaylist_Rule_Singular 
            : Nagi.WinUI.Resources.Strings.SmartPlaylist_Rule_Plural;
        
        return ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.SmartPlaylist_RuleSummary_Format, matchType, ruleCount, ruleWord);
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
    public override void ResetState()
    {
        _logger.LogDebug("Cleaning up SmartPlaylistSongListViewModel resources");

        _currentSmartPlaylist = null;
        _currentSmartPlaylistId = null;

        base.ResetState();
    }
}
