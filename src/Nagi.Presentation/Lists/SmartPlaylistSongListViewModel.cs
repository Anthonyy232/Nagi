using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Lists;

/// <summary>
///     A specialized song list view model for displaying songs that match a smart playlist's rules.
///     Unlike regular playlists, smart playlist songs are read-only and dynamically generated.
/// </summary>
public partial class SmartPlaylistSongListViewModel : DetailSongListViewModelBase<SmartPlaylistSongViewNavigationParameter>
{
    public static IReadOnlyList<SongSortOrder> SortOrders { get; } =
    [
        SongSortOrder.TitleAsc, SongSortOrder.TitleDesc, SongSortOrder.YearAsc, SongSortOrder.YearDesc,
        SongSortOrder.ArtistAsc, SongSortOrder.ArtistDesc, SongSortOrder.AlbumAsc, SongSortOrder.AlbumDesc,
        SongSortOrder.TrackNumberAsc, SongSortOrder.TrackNumberDesc, SongSortOrder.PlayCountAsc, SongSortOrder.PlayCountDesc,
        SongSortOrder.LastPlayedAsc, SongSortOrder.LastPlayedDesc, SongSortOrder.DateAddedAsc, SongSortOrder.DateAddedDesc,
        SongSortOrder.DurationAsc, SongSortOrder.DurationDesc, SongSortOrder.BpmAsc, SongSortOrder.BpmDesc,
        SongSortOrder.FileCreatedDateAsc, SongSortOrder.FileCreatedDateDesc,
        SongSortOrder.FileModifiedDateAsc, SongSortOrder.FileModifiedDateDesc, SongSortOrder.Random
    ];

    private readonly ISmartPlaylistService _smartPlaylistService;
    private Guid? _currentSmartPlaylistId;

    // Keeps a random order still across pages and play all, until the list is opened or sorted again.
    private int _shuffleSeed = Random.Shared.Next();

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
            if (edited is not null)
                await OpenAsync(new SmartPlaylistSongViewNavigationParameter
                {
                    Title = edited.Name,
                    SmartPlaylistId = smartPlaylistId,
                    CoverImageUri = edited.CoverUri
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit the rules of smart playlist {SmartPlaylistId}", smartPlaylistId);
        }
    }

    [ObservableProperty]
    public partial string RuleSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? CoverImageUri { get; set; }

    protected override string LoadErrorText => Strings.SmartPlaylist_ErrorLoading;

    // A smart playlist that no longer exists shows as an empty list under the name it was opened with.
    protected override async Task<bool> OpenDetailsAsync(SmartPlaylistSongViewNavigationParameter parameter,
        CancellationToken token)
    {
        Sort.Current = SongSortOrder.TitleAsc;
        _shuffleSeed = Random.Shared.Next();
        RuleSummary = string.Empty;
        PageTitle = parameter.Title;
        _currentSmartPlaylistId = parameter.SmartPlaylistId;
        CoverImageUri = parameter.CoverImageUri;

        var smartPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(parameter.SmartPlaylistId);
        if (token.IsCancellationRequested) return true;
        if (smartPlaylist is null)
        {
            _currentSmartPlaylistId = null;
            return true;
        }

        PageTitle = smartPlaylist.Name;
        CoverImageUri = smartPlaylist.CoverUri;
        RuleSummary = BuildRuleSummary(smartPlaylist);
        Sort.Current = smartPlaylist.SortOrder;
        return true;
    }

    private static string BuildRuleSummary(SmartPlaylist smartPlaylist)
    {
        var ruleCount = smartPlaylist.Rules.Count;
        if (ruleCount == 0)
            return Strings.SmartPlaylist_RuleSummary_NoRules;

        return ResourceFormatter.Format(smartPlaylist.MatchAllRules
            ? Strings.SmartPlaylist_RuleSummary_All_Format
            : Strings.SmartPlaylist_RuleSummary_Any_Format, ruleCount);
    }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (!_currentSmartPlaylistId.HasValue) return Task.FromResult(new PagedResult<Song>());
        return _smartPlaylistService.GetMatchingSongsPagedAsync(
            _currentSmartPlaylistId.Value, pageNumber, pageSize, ActiveSearchTerm, sortOrder, _shuffleSeed, cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (!_currentSmartPlaylistId.HasValue) return Task.FromResult(new List<Guid>());
        return _smartPlaylistService.GetMatchingSongIdsAsync(_currentSmartPlaylistId.Value, ActiveSearchTerm, sortOrder,
            _shuffleSeed, token);
    }

    protected override Task OnSortOrderChangedAsync()
    {
        _shuffleSeed = Random.Shared.Next();
        return base.OnSortOrderChangedAsync();
    }

    protected override PlaybackContext GetPlaybackContext() =>
        _currentSmartPlaylistId.HasValue ? new(PlaybackContextType.SmartPlaylist, _currentSmartPlaylistId.Value) : base.GetPlaybackContext();

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _currentSmartPlaylistId.HasValue
            ? _smartPlaylistService.SetSortOrderAsync(_currentSmartPlaylistId.Value, sortOrder)
            : Task.CompletedTask;
    }

    private string? ActiveSearchTerm => IsSearchActive ? SearchTerm : null;

    public override void ResetState()
    {
        _currentSmartPlaylistId = null;

        base.ResetState();
    }
}
