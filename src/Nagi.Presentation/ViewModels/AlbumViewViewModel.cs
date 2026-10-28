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
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

public partial class AlbumViewViewModel : SongListViewModelBase
{
    private Guid _albumId;
    private int? _albumYear;
    private int _totalSongCount;
    private TimeSpan _totalDuration;
    private CancellationTokenSource? _durationFetchCts;

    public AlbumViewViewModel(
        ILibraryReader libraryReader,
        ILibraryWriter libraryWriter,
        ILibraryScanner libraryScanner,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<AlbumViewViewModel> logger)
        : base(libraryReader, libraryWriter, libraryScanner, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
    {
        AlbumTitle = Strings.AlbumView_DefaultAlbumTitle;
        ArtistName = Strings.AlbumView_DefaultArtistName;
        AlbumDetailsText = string.Empty;

        CurrentSortOrder = SongSortOrder.TrackNumberAsc;
    }

    [ObservableProperty] public partial string AlbumTitle { get; set; }

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
        return IsSearchActive
            ? _libraryReader.SearchSongsInAlbumPagedAsync(_albumId, SearchTerm, pageNumber, pageSize, cancellationToken)
            : _libraryReader.GetSongsByAlbumIdPagedAsync(_albumId, pageNumber, pageSize, sortOrder, cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (_albumId == Guid.Empty) return Task.FromResult(new List<Guid>());
        return IsSearchActive
            ? _libraryReader.SearchAllSongIdsInAlbumAsync(_albumId, SearchTerm, sortOrder, token)
            : _libraryReader.GetAllSongIdsByAlbumIdAsync(_albumId, sortOrder, token);
    }

    public async Task LoadAlbumDetailsAsync(Guid albumId)
    {
        if (_isDisposed) return;
        ResetState();
        var generation = CaptureSongContext();

        _logger.LogDebug("Loading details for album {AlbumId}", albumId);
        try
        {
            _albumId = albumId;
            var albumTask = _libraryReader.GetAlbumByIdAsync(albumId);
            var sortOrderTask = _settingsService.GetSortOrderAsync<SongSortOrder>(SortOrderHelper.AlbumViewSortOrderKey);

            await Task.WhenAll(albumTask, sortOrderTask);
            if (!IsSongContextCurrent(generation)) return;
            CurrentSortOrder = await sortOrderTask;
            var album = await albumTask;

            if (album != null)
            {
                AlbumTitle = album.Title;
                ArtistName = album.ArtistName;
                AlbumArtists = album.AlbumArtists;
                PageTitle = album.Title;
                _albumYear = album.Year;
                CoverArtUri = ImageUriHelper.GetUriWithCacheBuster(album.CoverArtUri);
                await RefreshOrSortSongsAsync();
                _logger.LogDebug("Populated details for album '{AlbumTitle}' ({AlbumId})", album.Title, albumId);
            }
            else
            {
                HandleAlbumNotFound(albumId);
            }
        }
        catch (Exception ex)
        {
            if (IsSongContextCurrent(generation)) HandleLoadError(albumId, ex);
        }
    }

    private void HandleAlbumNotFound(Guid albumId)
    {
        _logger.LogWarning("Album with ID {AlbumId} not found", albumId);
        AlbumTitle = Strings.AlbumView_AlbumNotFound;
        PageTitle = Strings.Generic_NoItems;
        ArtistName = string.Empty;
        AlbumArtists = new List<AlbumArtist>();
        CoverArtUri = null;
        Songs.Clear();
        GroupedSongsFlat.Clear();
        AlbumDetailsText = string.Empty;
        TotalItemsText = ResourceFormatter.Format(Strings.Songs_Count_Plural, 0);
    }

    private void HandleLoadError(Guid albumId, Exception ex)
    {
        _logger.LogError(ex, "Failed to load album with ID {AlbumId}", albumId);
        AlbumTitle = Strings.AlbumView_Error;
        PageTitle = Strings.Generic_Error;
        ArtistName = string.Empty;
        AlbumArtists = new List<AlbumArtist>();
        CoverArtUri = null;
        AlbumDetailsText = string.Empty;
        TotalItemsText = Strings.Generic_Error;
        Songs.Clear();
        GroupedSongsFlat.Clear();
        HasLoadError = true;
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
                () => _libraryReader.GetSearchTotalDurationInAlbumAsync(albumId, searchTerm, cancellationToken),
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
        IsGroupedByDisc = CurrentSortOrder == SongSortOrder.TrackNumberAsc && !IsSearchActive;

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

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        return _settingsService.SetSortOrderAsync(SortOrderHelper.AlbumViewSortOrderKey, sortOrder);
    }

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
    public string Title => string.Format(Strings.AlbumView_DiscHeader, DiscNumber);

    public override string ToString() => Title;
}
