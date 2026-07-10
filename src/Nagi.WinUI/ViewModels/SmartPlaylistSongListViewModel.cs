using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
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
    private readonly record struct RandomSnapshotKey(Guid PlaylistId, string? SearchTerm);

    private readonly ISmartPlaylistService _smartPlaylistService;
    private readonly object _randomSnapshotLock = new();
    private Guid? _currentSmartPlaylistId;
    private Task<List<Guid>>? _randomSongIdsTask;
    private RandomSnapshotKey? _randomSnapshotKey;

    public SmartPlaylistSongListViewModel(
        ILibraryService libraryService,
        IPlaylistService playlistService,
        ISmartPlaylistService smartPlaylistService,
        IMusicPlaybackService playbackService,
        INavigationService navigationService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<SmartPlaylistSongListViewModel> logger)
        : base(libraryService, playlistService, playbackService, navigationService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
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
        if (IsLoading) return;
        _logger.LogDebug("Initializing for smart playlist '{Title}' (ID: {SmartPlaylistId})", title, smartPlaylistId);

        try
        {
            PageTitle = title;
            _currentSmartPlaylistId = smartPlaylistId;
            CoverImageUri = coverImageUri;
            ClearRandomSnapshot();

            if (smartPlaylistId.HasValue)
            {
                var smartPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(smartPlaylistId.Value);
                if (smartPlaylist != null)
                {
                    RuleSummary = BuildRuleSummary(smartPlaylist);
                    CurrentSortOrder = SortOrderHelper.MapToSongSortOrder(smartPlaylist.SortOrder);
                    UpdateSortOrderButtonText(CurrentSortOrder);
                }
            }

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
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
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (!_currentSmartPlaylistId.HasValue) return new PagedResult<Song>();

        if (sortOrder == SongSortOrder.Random)
            return await LoadRandomSongsPageAsync(pageNumber, pageSize, cancellationToken).ConfigureAwait(false);

        return await _smartPlaylistService.GetMatchingSongsPagedAsync(
            _currentSmartPlaylistId.Value, pageNumber, pageSize, ActiveSearchTerm, sortOrder, cancellationToken);
    }

    protected override async Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (!_currentSmartPlaylistId.HasValue) return new List<Guid>();
        if (sortOrder == SongSortOrder.Random) return await GetRandomSongIdsAsync(token).ConfigureAwait(false);
        return await _smartPlaylistService.GetMatchingSongIdsAsync(_currentSmartPlaylistId.Value, ActiveSearchTerm, sortOrder, token);
    }

    protected override PlaybackContext GetPlaybackContext() =>
        _currentSmartPlaylistId.HasValue ? new(PlaybackContextType.SmartPlaylist, _currentSmartPlaylistId.Value) : base.GetPlaybackContext();

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _currentSmartPlaylistId.HasValue
            ? _smartPlaylistService.SetSortOrderAsync(_currentSmartPlaylistId.Value, SortOrderHelper.MapToSmartPlaylistSortOrder(sortOrder))
            : Task.CompletedTask;
    }

    public override async Task RefreshOrSortSongsAsync(string? sortOrderString = null, CancellationToken manualToken = default)
    {
        ClearRandomSnapshot();
        await base.RefreshOrSortSongsAsync(sortOrderString, manualToken);
    }

    private async Task<PagedResult<Song>> LoadRandomSongsPageAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        var ids = await GetRandomSongIdsAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var pageIds = ids.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        var songsById = await _libraryReader.GetSongsByIdsAsync(pageIds).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var items = pageIds
            .Select(id => songsById.TryGetValue(id, out var song) ? song : null)
            .Where(song => song is not null)
            .Select(song => song!)
            .ToList();

        return new PagedResult<Song>
        {
            Items = items,
            TotalCount = ids.Count,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    private async Task<List<Guid>> GetRandomSongIdsAsync(CancellationToken token)
    {
        if (!_currentSmartPlaylistId.HasValue) return new List<Guid>();

        var playlistId = _currentSmartPlaylistId.Value;
        var searchTerm = ActiveSearchTerm;
        var snapshotKey = new RandomSnapshotKey(playlistId, searchTerm);
        Task<List<Guid>> snapshotTask;

        lock (_randomSnapshotLock)
        {
            if (_randomSongIdsTask is null || _randomSnapshotKey != snapshotKey)
            {
                _randomSnapshotKey = snapshotKey;
                // The ordering is shared by every page. Do not let cancellation of the first page
                // poison the snapshot for subsequent page or playback-ID requests.
                _randomSongIdsTask = _smartPlaylistService.GetMatchingSongIdsAsync(
                    playlistId, searchTerm, SongSortOrder.Random, CancellationToken.None);
            }

            snapshotTask = _randomSongIdsTask;
        }

        try
        {
            return await snapshotTask.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ClearRandomSnapshot(snapshotTask);
            throw;
        }
    }

    private string? ActiveSearchTerm => IsSearchActive ? SearchTerm : null;

    private void ClearRandomSnapshot(Task<List<Guid>>? expectedTask = null)
    {
        lock (_randomSnapshotLock)
        {
            if (expectedTask is not null && !ReferenceEquals(_randomSongIdsTask, expectedTask))
                return;

            _randomSongIdsTask = null;
            _randomSnapshotKey = null;
        }
    }

    private static void SanitizePaging(ref int pageNumber, ref int pageSize)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 500) pageSize = 500;
    }

    /// <summary>
    ///     Cleans up resources specific to this view model.
    /// </summary>
    public override void ResetState()
    {
        _logger.LogDebug("Cleaning up SmartPlaylistSongListViewModel resources");

        _currentSmartPlaylistId = null;
        ClearRandomSnapshot();

        base.ResetState();
    }
}
