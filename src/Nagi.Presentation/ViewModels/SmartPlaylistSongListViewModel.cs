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
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

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
    private int _playlistGeneration;
    private Task<List<Guid>>? _randomSongIdsTask;
    private RandomSnapshotKey? _randomSnapshotKey;

    public SmartPlaylistSongListViewModel(
        ILibraryReader libraryReader,
        ILibraryWriter libraryWriter,
        ILibraryScanner libraryScanner,
        IPlaylistService playlistService,
        ISmartPlaylistService smartPlaylistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<SmartPlaylistSongListViewModel> logger)
        : base(libraryReader, libraryWriter, libraryScanner, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
    {
        _smartPlaylistService = smartPlaylistService;
    }




    public Guid? GetSmartPlaylistId() => _currentSmartPlaylistId;

    /// <summary>
    ///     Opens the smart playlist editor for this playlist and reloads it if the user saved changes.
    /// </summary>
    [RelayCommand]
    private async Task EditRulesAsync()
    {
        if (_currentSmartPlaylistId is not { } smartPlaylistId) return;

        var smartPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(smartPlaylistId);
        if (smartPlaylist is null)
        {
            _logger.LogWarning("Could not find smart playlist with ID {SmartPlaylistId}", smartPlaylistId);
            return;
        }

        var edited = await _uiService.ShowSmartPlaylistEditorAsync(smartPlaylist);
        if (edited is not null) await InitializeAsync(edited.Name, smartPlaylistId, edited.CoverImageUri);
    }

    [ObservableProperty]
    public partial string RuleSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? CoverImageUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverImageUri);



    public async Task InitializeAsync(string title, Guid? smartPlaylistId, string? coverImageUri = null)
    {
        if (_isDisposed) return;
        ResetState();
        var generation = _playlistGeneration;
        CurrentPage = 1;
        CurrentSortOrder = SongSortOrder.TitleAsc;
        Songs.Clear();
        TotalItemCount = 0;
        RuleSummary = string.Empty;
        _logger.LogDebug("Initializing for smart playlist '{Title}' (ID: {SmartPlaylistId})", title, smartPlaylistId);

        try
        {
            PageTitle = title;
            _currentSmartPlaylistId = smartPlaylistId;
            CoverImageUri = coverImageUri;

            if (smartPlaylistId.HasValue)
            {
                var smartPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(smartPlaylistId.Value);
                if (_isDisposed || generation != _playlistGeneration) return;
                if (smartPlaylist != null)
                {
                    PageTitle = smartPlaylist.Name;
                    CoverImageUri = ImageUriHelper.GetUriWithCacheBuster(smartPlaylist.CoverImageUri, smartPlaylist.DateModified);
                    RuleSummary = BuildRuleSummary(smartPlaylist);
                    CurrentSortOrder = SortOrderHelper.MapToSongSortOrder(smartPlaylist.SortOrder);
                }
                else
                {
                    _currentSmartPlaylistId = null;
                }
            }

            await RefreshOrSortSongsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            if (_isDisposed || generation != _playlistGeneration) return;
            _logger.LogError(ex, "Failed to initialize smart playlist {SmartPlaylistId}", _currentSmartPlaylistId);
            TotalItemsText = Nagi.Presentation.Resources.Strings.SmartPlaylist_ErrorLoading;
            Songs.Clear();
        }
    }

    private static string BuildRuleSummary(SmartPlaylist smartPlaylist)
    {
        var ruleCount = smartPlaylist.Rules.Count;
        if (ruleCount == 0)
            return Nagi.Presentation.Resources.Strings.SmartPlaylist_RuleSummary_NoRules;

        var matchType = smartPlaylist.MatchAllRules
            ? Nagi.Presentation.Resources.Strings.SmartPlaylist_MatchAll
            : Nagi.Presentation.Resources.Strings.SmartPlaylist_MatchAny;
        var ruleWord = ruleCount == 1
            ? Nagi.Presentation.Resources.Strings.SmartPlaylist_Rule_Singular
            : Nagi.Presentation.Resources.Strings.SmartPlaylist_Rule_Plural;

        return ResourceFormatter.Format(Nagi.Presentation.Resources.Strings.SmartPlaylist_RuleSummary_Format, matchType, ruleCount, ruleWord);
    }


    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (!_currentSmartPlaylistId.HasValue) return Task.FromResult(new PagedResult<Song>());

        if (sortOrder == SongSortOrder.Random)
            return LoadRandomSongsPageAsync(pageNumber, pageSize, cancellationToken);

        return _smartPlaylistService.GetMatchingSongsPagedAsync(
            _currentSmartPlaylistId.Value, pageNumber, pageSize, ActiveSearchTerm, sortOrder, cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (!_currentSmartPlaylistId.HasValue) return Task.FromResult(new List<Guid>());
        if (sortOrder == SongSortOrder.Random) return GetRandomSongIdsAsync(token);
        return _smartPlaylistService.GetMatchingSongIdsAsync(_currentSmartPlaylistId.Value, ActiveSearchTerm, sortOrder, token);
    }

    protected override PlaybackContext GetPlaybackContext() =>
        _currentSmartPlaylistId.HasValue ? new(PlaybackContextType.SmartPlaylist, _currentSmartPlaylistId.Value) : base.GetPlaybackContext();

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _currentSmartPlaylistId.HasValue
            ? _smartPlaylistService.SetSortOrderAsync(_currentSmartPlaylistId.Value, SortOrderHelper.MapToSmartPlaylistSortOrder(sortOrder))
            : Task.CompletedTask;
    }

    public override Task RefreshOrSortSongsAsync(string? sortOrderString = null, CancellationToken manualToken = default)
    {
        ClearRandomSnapshot();
        return base.RefreshOrSortSongsAsync(sortOrderString, manualToken);
    }

    private async Task<PagedResult<Song>> LoadRandomSongsPageAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        var ids = await GetRandomSongIdsAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var pageIds = ids.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        var songsById = await _libraryReader.GetSongsByIdsAsync(pageIds).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var items = pageIds
            .Select(id => songsById.TryGetValue(id, out var song) ? song : null)
            .OfType<Song>()
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
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
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

    public override void ResetState()
    {
        _logger.LogDebug("Cleaning up SmartPlaylistSongListViewModel resources");

        _currentSmartPlaylistId = null;
        _playlistGeneration++;
        ClearRandomSnapshot();

        base.ResetState();
    }
}
