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
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     A specialized song list view model for displaying songs that match a smart playlist's rules.
///     Unlike regular playlists, smart playlist songs are read-only and dynamically generated.
/// </summary>
public partial class SmartPlaylistSongListViewModel : SongListViewModelBase
{
    private static readonly SongSortOrder[] SortOrders =
    [
        SongSortOrder.TitleAsc, SongSortOrder.TitleDesc, SongSortOrder.YearAsc, SongSortOrder.YearDesc,
        SongSortOrder.ArtistAsc, SongSortOrder.ArtistDesc, SongSortOrder.AlbumAsc, SongSortOrder.AlbumDesc,
        SongSortOrder.TrackNumberAsc, SongSortOrder.TrackNumberDesc, SongSortOrder.PlayCountAsc, SongSortOrder.PlayCountDesc,
        SongSortOrder.LastPlayedAsc, SongSortOrder.LastPlayedDesc, SongSortOrder.DateAddedAsc, SongSortOrder.DateAddedDesc,
        SongSortOrder.DurationAsc, SongSortOrder.DurationDesc, SongSortOrder.BpmAsc, SongSortOrder.BpmDesc,
        SongSortOrder.FileCreatedDateAsc, SongSortOrder.FileCreatedDateDesc, SongSortOrder.Random
    ];

    private readonly ISmartPlaylistService _smartPlaylistService;
    private Guid? _currentSmartPlaylistId;
    private int _playlistGeneration;

    public SmartPlaylistSongListViewModel(
        ILibraryReader libraryReader,
        IListeningService listening,
        ILibraryNotifier libraryNotifier,
        IPlaylistService playlistService,
        ISmartPlaylistService smartPlaylistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        ISettingsService settingsService,
        IUIService uiService,
        ILogger<SmartPlaylistSongListViewModel> logger)
        : base(libraryReader, listening, libraryNotifier, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger, SortOrders, null)
    {
        _smartPlaylistService = smartPlaylistService;
    }

    /// <summary>
    ///     Opens the smart playlist editor for this playlist and reloads it if the user saved changes.
    /// </summary>
    [RelayCommand]
    private async Task EditRulesAsync()
    {
        if (_currentSmartPlaylistId is not { } smartPlaylistId) return;

        try
        {
            var smartPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(smartPlaylistId);
            if (smartPlaylist is null)
            {
                _logger.LogWarning("Could not find smart playlist with ID {SmartPlaylistId}", smartPlaylistId);
                return;
            }

            var edited = await _uiService.ShowSmartPlaylistEditorAsync(smartPlaylist);
            if (edited is not null) await InitializeAsync(edited.Name, smartPlaylistId, edited.CoverImageUri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit the rules of smart playlist {SmartPlaylistId}", smartPlaylistId);
        }
    }

    [ObservableProperty]
    public partial string RuleSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? CoverImageUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverImageUri);

    public async Task InitializeAsync(string title, Guid smartPlaylistId, string? coverImageUri = null)
    {
        if (_isDisposed) return;
        ResetState();
        var generation = _playlistGeneration;
        CurrentPage = 1;
        Sort.Current = SongSortOrder.TitleAsc;
        Songs.Clear();
        TotalItemCount = 0;
        RuleSummary = string.Empty;
        _logger.LogDebug("Initializing for smart playlist '{Title}' (ID: {SmartPlaylistId})", title, smartPlaylistId);

        try
        {
            PageTitle = title;
            _currentSmartPlaylistId = smartPlaylistId;
            CoverImageUri = coverImageUri;

            var smartPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(smartPlaylistId);
            if (_isDisposed || generation != _playlistGeneration) return;
            if (smartPlaylist != null)
            {
                PageTitle = smartPlaylist.Name;
                CoverImageUri = ImageUriHelper.GetUriWithCacheBuster(smartPlaylist.CoverImageUri, smartPlaylist.DateModified);
                RuleSummary = BuildRuleSummary(smartPlaylist);
                Sort.Current = smartPlaylist.SortOrder.ToSongSortOrder();
            }
            else
            {
                _currentSmartPlaylistId = null;
            }

            await LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (_isDisposed || generation != _playlistGeneration) return;
            _logger.LogError(ex, "Failed to initialize smart playlist {SmartPlaylistId}", _currentSmartPlaylistId);
            TotalItemsText = Strings.SmartPlaylist_ErrorLoading;
            Songs.Clear();
        }
    }

    private static string BuildRuleSummary(SmartPlaylist smartPlaylist)
    {
        var ruleCount = smartPlaylist.Rules.Count;
        if (ruleCount == 0)
            return Strings.SmartPlaylist_RuleSummary_NoRules;

        var matchType = smartPlaylist.MatchAllRules
            ? Strings.SmartPlaylist_MatchAll
            : Strings.SmartPlaylist_MatchAny;
        var ruleWord = ruleCount == 1
            ? Strings.SmartPlaylist_Rule_Singular
            : Strings.SmartPlaylist_Rule_Plural;

        return ResourceFormatter.Format(Strings.SmartPlaylist_RuleSummary_Format, matchType, ruleCount, ruleWord);
    }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (!_currentSmartPlaylistId.HasValue) return Task.FromResult(new PagedResult<Song>());

        if (sortOrder == SongSortOrder.Random)
            return LoadRandomPageAsync(pageNumber, pageSize, cancellationToken);

        return _smartPlaylistService.GetMatchingSongsPagedAsync(
            _currentSmartPlaylistId.Value, pageNumber, pageSize, ActiveSearchTerm, sortOrder, cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (!_currentSmartPlaylistId.HasValue) return Task.FromResult(new List<Guid>());
        return _smartPlaylistService.GetMatchingSongIdsAsync(_currentSmartPlaylistId.Value, ActiveSearchTerm, sortOrder, token);
    }

    protected override PlaybackContext GetPlaybackContext() =>
        _currentSmartPlaylistId.HasValue ? new(PlaybackContextType.SmartPlaylist, _currentSmartPlaylistId.Value) : base.GetPlaybackContext();

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _currentSmartPlaylistId.HasValue
            ? _smartPlaylistService.SetSortOrderAsync(_currentSmartPlaylistId.Value, sortOrder.ToSmartPlaylistSortOrder())
            : Task.CompletedTask;
    }

    // A random order only holds still as one list of IDs, so every page is cut from the list that playing all uses.
    private async Task<PagedResult<Song>> LoadRandomPageAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        var ids = await GetFullSongIdsAsync().WaitAsync(token).ConfigureAwait(false);

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

    private string? ActiveSearchTerm => IsSearchActive ? SearchTerm : null;

    public override void ResetState()
    {
        _logger.LogDebug("Cleaning up SmartPlaylistSongListViewModel resources");

        _currentSmartPlaylistId = null;
        _playlistGeneration++;

        base.ResetState();
    }
}
