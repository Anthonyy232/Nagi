using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;
using Nagi.Core.Settings;

namespace Nagi.Presentation.ViewModels;

public partial class AlbumViewViewModel : DetailSongListViewModelBase<AlbumViewNavigationParameter>
{
    private Guid _albumId;
    private int? _albumYear;
    private int _totalSongCount;
    private TimeSpan _totalDuration;
    private CancellationTokenSource? _durationFetchCts;

    public AlbumViewViewModel(
        ILibraryReader libraryReader,
        IListeningService listening,
        ILibraryNotifier libraryNotifier,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        ISettingsService settingsService,
        IUIService uiService,
        ILogger<AlbumViewViewModel> logger)
        : base(libraryReader, listening, libraryNotifier, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger, SortOrders, AppSettings.AlbumViewSortOrder)
    {
        PageTitle = Strings.AlbumView_DefaultAlbumTitle;
        ArtistName = Strings.AlbumView_DefaultArtistName;
        AlbumDetailsText = string.Empty;
    }

    private static readonly SongSortOrder[] SortOrders =
    [
        SongSortOrder.TrackNumberAsc, SongSortOrder.TrackNumberDesc, SongSortOrder.TitleAsc, SongSortOrder.TitleDesc,
        SongSortOrder.YearAsc, SongSortOrder.YearDesc, SongSortOrder.FileCreatedDateAsc, SongSortOrder.FileCreatedDateDesc
    ];

    [ObservableProperty] public partial string ArtistName { get; set; }

    [ObservableProperty] public partial ICollection<AlbumArtist> AlbumArtists { get; set; } = new List<AlbumArtist>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtworkAvailable))]
    public partial string? CoverArtUri { get; set; }

    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverArtUri);

    [ObservableProperty] public partial string AlbumDetailsText { get; set; }

    public ObservableRangeCollection<object> GroupedSongsFlat { get; } = new();

    [ObservableProperty] public partial bool IsGroupedByDisc { get; set; }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (_albumId == Guid.Empty) return Task.FromResult(new PagedResult<Song>());
        return _libraryReader.GetSongsAsync(new SongScope.Album(_albumId), SearchTerm, sortOrder, pageNumber, pageSize,
            cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (_albumId == Guid.Empty) return Task.FromResult(new List<Guid>());
        return _libraryReader.GetSongIdsAsync(new SongScope.Album(_albumId), SearchTerm, sortOrder, token);
    }

    protected override string NotFoundTitle => Strings.AlbumView_AlbumNotFound;

    protected override string OpenErrorTitle => Strings.AlbumView_Error;

    protected override async Task<bool> OpenDetailsAsync(AlbumViewNavigationParameter parameter, CancellationToken token)
    {
        _albumId = parameter.AlbumId;
        var album = await _libraryReader.GetAlbumByIdAsync(parameter.AlbumId);
        if (album is null || token.IsCancellationRequested) return album is not null;

        PageTitle = album.Title;
        ArtistName = album.ArtistName;
        AlbumArtists = album.AlbumArtists;
        _albumYear = album.Year;
        CoverArtUri = ImageUriHelper.GetUriWithCacheBuster(album.CoverArtUri);
        return true;
    }

    protected override void ClearDetails()
    {
        ArtistName = string.Empty;
        AlbumArtists = new List<AlbumArtist>();
        CoverArtUri = null;
        AlbumDetailsText = string.Empty;
        GroupedSongsFlat.Clear();
    }

    private void UpdateAlbumDetails(PagedResult<Song> pagedResult)
    {
        _totalSongCount = pagedResult.TotalCount;
        CancelDurationFetch();
        _totalDuration = TimeSpan.FromTicks(pagedResult.Items.Sum(s => s.DurationTicks));
        RefreshAlbumDetailsText();

        if (pagedResult.HasNextPage)
        {
            _durationFetchCts = new CancellationTokenSource();
            _ = UpdateTotalDurationAsync(_albumId, SearchTerm, _durationFetchCts.Token);
        }
    }

    private async Task UpdateTotalDurationAsync(Guid albumId, string searchTerm, CancellationToken cancellationToken)
    {
        try
        {
            var duration = await Task.Run(
                () => _libraryReader.GetSongsDurationAsync(new SongScope.Album(albumId), searchTerm, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            _dispatcherService.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested || _isDisposed) return;
                _totalDuration = duration;
                RefreshAlbumDetailsText();
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when user changes search or navigates away
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch total duration for album {AlbumId}", albumId);
        }
    }

    private void RefreshAlbumDetailsText()
    {
        var detailsParts = new List<string>();

        if (_albumYear.HasValue)
            detailsParts.Add(_albumYear.Value.ToString());

        var songCountText = _totalSongCount == 1
            ? ResourceFormatter.Format(Strings.AlbumView_SongCount_Singular, _totalSongCount)
            : ResourceFormatter.Format(Strings.AlbumView_SongCount_Plural, _totalSongCount);
        detailsParts.Add(songCountText);

        if (_totalDuration > TimeSpan.Zero) detailsParts.Add(DurationFormatter.Format(_totalDuration));

        AlbumDetailsText = string.Join(" • ", detailsParts);
    }

    protected override void ApplyItemsToCollection(PagedResult<Song> result, bool append)
    {
        base.ApplyItemsToCollection(result, append);
        if (!append && result.PageNumber == 1) UpdateAlbumDetails(result);
        UpdateGrouping();
    }

    private void UpdateGrouping()
    {
        IsGroupedByDisc = Sort.Current == SongSortOrder.TrackNumberAsc && !IsSearchActive;

        if (IsGroupedByDisc)
        {
            var tempList = new List<object>();

            var groups = Songs
                .GroupBy(s => s.DiscNumber ?? -1)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                if (group.Key > 0)
                {
                    tempList.Add(new DiscHeader { DiscNumber = group.Key });
                }

                tempList.AddRange(group);
            }

            GroupedSongsFlat.ReplaceRange(tempList);
        }
        else
        {
            GroupedSongsFlat.Clear();
        }
    }

    // Track numbers always show because they give an album its order.
    public override SongListColumns VisibleColumns =>
        (ChosenColumns & ~(SongListColumns.Artist | SongListColumns.Album | SongListColumns.Year)) | SongListColumns.TrackNumber;

    protected override PlaybackContext GetPlaybackContext() =>
        _albumId != Guid.Empty ? new(PlaybackContextType.Album, _albumId) : base.GetPlaybackContext();

    private void CancelDurationFetch()
    {
        _durationFetchCts?.Cancel();
        _durationFetchCts?.Dispose();
        _durationFetchCts = null;
    }

    protected override void OnSearchTermChangedInternal(string value)
    {
        CancelDurationFetch();
        base.OnSearchTermChangedInternal(value);
    }

    public override void ResetState()
    {
        CancelDurationFetch();
        base.ResetState();
    }
}

public class DiscHeader : IListHeader
{
    public int DiscNumber { get; set; }
    public string Title => ResourceFormatter.Format(Strings.AlbumView_DiscHeader, DiscNumber);

    public override string ToString() => Title;
}
