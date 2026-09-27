using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Resources;
using Nagi.WinUI.Services.Abstractions;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Represents a single segment in a horizontal stacked bar
///     (e.g. playback source distribution).
/// </summary>
public sealed class SourceSegment
{
    public string Label { get; init; } = string.Empty;
    public double Percentage { get; init; }
    public string PercentageText { get; init; } = string.Empty;
    public Brush SegmentBrush { get; init; } = new SolidColorBrush(Colors.Gray);
}

/// <summary>
///     A display-friendly wrapper for top-song statistics shown in the insights cards.
/// </summary>
public sealed class TopSongItem
{
    public int Rank { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string? ArtworkUri { get; init; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(ArtworkUri);
    public string StatText { get; init; } = string.Empty;
}

/// <summary>
///     A display-friendly wrapper for top-artist statistics.
/// </summary>
public sealed class TopArtistItem
{
    public Guid ArtistId { get; init; }
    public int Rank { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? ImageUri { get; init; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(ImageUri);
    public string StatText { get; init; } = string.Empty;
    public IRelayCommand<TopArtistItem>? Command { get; init; }
}

/// <summary>
///     A display-friendly wrapper for top-album statistics.
/// </summary>
public sealed class TopAlbumItem
{
    public Guid AlbumId { get; init; }
    public int Rank { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ArtistName { get; init; } = string.Empty;
    public string? ArtworkUri { get; init; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(ArtworkUri);
    public string StatText { get; init; } = string.Empty;
    public IRelayCommand<TopAlbumItem>? Command { get; init; }
}

/// <summary>
///     A display-friendly wrapper for top-genre statistics.
/// </summary>
public sealed class TopGenreItem
{
    public Guid GenreId { get; init; }
    public int Rank { get; init; }
    public string Name { get; init; } = string.Empty;
    public string StatText { get; init; } = string.Empty;
    public IRelayCommand<TopGenreItem>? Command { get; init; }
}

/// <summary>
///     Identifies which category is currently shown in the "See All" overlay.
/// </summary>
public enum SeeAllCategory { None, Songs, Artists, Albums, Genres }

/// <summary>
///     Manages the state and logic for the Listening Insights dashboard page.
/// </summary>
public partial class InsightsViewModel : ObservableObject
{
    private readonly IStatisticsService _statisticsService;
    private readonly IDispatcherService _dispatcherService;
    private readonly INavigationService _navigationService;
    private readonly ILibraryService _libraryService;
    private readonly IUIService _uiService;
    private readonly ILogger<InsightsViewModel> _logger;
    private readonly Debouncer _seeAllSearchDebouncer = new(TimeSpan.FromMilliseconds(300));
    private CancellationTokenSource? _seeAllCts;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _songsSortCts;
    private CancellationTokenSource? _artistsSortCts;
    private CancellationTokenSource? _albumsSortCts;
    private CancellationTokenSource? _genresSortCts;

    public InsightsViewModel(
        IStatisticsService statisticsService,
        IDispatcherService dispatcherService,
        INavigationService navigationService,
        ILibraryService libraryService,
        IUIService uiService,
        ILogger<InsightsViewModel> logger)
    {
        _statisticsService = statisticsService;
        _dispatcherService = dispatcherService;
        _navigationService = navigationService;
        _libraryService = libraryService;
        _uiService = uiService;
        _logger = logger;
    }

    // ── Time range ──────────────────────────────────────────────

    /// <summary>
    ///     The available time range presets shown in the segmented control.
    /// </summary>
    public IReadOnlyList<string> TimeRangeLabels { get; } =
        new[]
        {
            Strings.Insights_TimeRange_Last1Day,
            Strings.Insights_TimeRange_Last7Days,
            Strings.Insights_TimeRange_Last30Days,
            Strings.Insights_TimeRange_Last90Days,
            Strings.Insights_TimeRange_LastYear,
            Strings.Insights_TimeRange_AllTime
        };

    [ObservableProperty] public partial int SelectedTimeRangeIndex { get; set; } = 2; // default 30D

    async partial void OnSelectedTimeRangeIndexChanged(int value)
    {
        if (IsSeeAllOpen)
            await CloseSeeAll();
        await LoadInsightsAsync();
    }

    // ── Loading state ───────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool HasData { get; set; }

    public bool ShowEmptyState => !IsLoading && !HasData;

    // ── Hero stats ──────────────────────────────────────────────

    [ObservableProperty] public partial string TotalListenTimeText { get; set; } = "—";
    [ObservableProperty] public partial int UniqueSongsPlayed { get; set; }
    [ObservableProperty] public partial string PeakHourText { get; set; } = "—";
    [ObservableProperty] public partial string MostActiveDayText { get; set; } = "—";

    // ── Sort metrics ─────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSongsByPlayCount), nameof(IsSongsByDuration), nameof(SongsSortLabel), nameof(SeeAllSortLabel), nameof(IsSeeAllByPlayCount), nameof(IsSeeAllByDuration))]
    public partial SortMetric SongsSortMetric { get; set; } = SortMetric.PlayCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtistsByPlayCount), nameof(IsArtistsByDuration), nameof(ArtistsSortLabel), nameof(SeeAllSortLabel), nameof(IsSeeAllByPlayCount), nameof(IsSeeAllByDuration))]
    public partial SortMetric ArtistsSortMetric { get; set; } = SortMetric.Duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlbumsByPlayCount), nameof(IsAlbumsByDuration), nameof(AlbumsSortLabel), nameof(SeeAllSortLabel), nameof(IsSeeAllByPlayCount), nameof(IsSeeAllByDuration))]
    public partial SortMetric AlbumsSortMetric { get; set; } = SortMetric.PlayCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGenresByPlayCount), nameof(IsGenresByDuration), nameof(GenresSortLabel), nameof(SeeAllSortLabel), nameof(IsSeeAllByPlayCount), nameof(IsSeeAllByDuration))]
    public partial SortMetric GenresSortMetric { get; set; } = SortMetric.PlayCount;

    public bool IsSongsByPlayCount => SongsSortMetric == SortMetric.PlayCount;
    public bool IsSongsByDuration => SongsSortMetric == SortMetric.Duration;
    public bool IsArtistsByPlayCount => ArtistsSortMetric == SortMetric.PlayCount;
    public bool IsArtistsByDuration => ArtistsSortMetric == SortMetric.Duration;
    public bool IsAlbumsByPlayCount => AlbumsSortMetric == SortMetric.PlayCount;
    public bool IsAlbumsByDuration => AlbumsSortMetric == SortMetric.Duration;
    public bool IsGenresByPlayCount => GenresSortMetric == SortMetric.PlayCount;
    public bool IsGenresByDuration => GenresSortMetric == SortMetric.Duration;

    public string SongsSortLabel => SongsSortMetric == SortMetric.PlayCount ? Strings.InsightsPage_SortBy_PlayCount : Strings.InsightsPage_SortBy_Duration;
    public string ArtistsSortLabel => ArtistsSortMetric == SortMetric.PlayCount ? Strings.InsightsPage_SortBy_PlayCount : Strings.InsightsPage_SortBy_Duration;
    public string AlbumsSortLabel => AlbumsSortMetric == SortMetric.PlayCount ? Strings.InsightsPage_SortBy_PlayCount : Strings.InsightsPage_SortBy_Duration;
    public string GenresSortLabel => GenresSortMetric == SortMetric.PlayCount ? Strings.InsightsPage_SortBy_PlayCount : Strings.InsightsPage_SortBy_Duration;

    // See All overlay sort state (reflects whichever category is currently open).
    public string SeeAllSortLabel => CurrentSeeAllCategory switch
    {
        SeeAllCategory.Songs => SongsSortLabel,
        SeeAllCategory.Artists => ArtistsSortLabel,
        SeeAllCategory.Albums => AlbumsSortLabel,
        SeeAllCategory.Genres => GenresSortLabel,
        _ => Strings.InsightsPage_SortBy_PlayCount
    };
    public bool IsSeeAllByPlayCount => CurrentSeeAllCategory switch
    {
        SeeAllCategory.Songs => SongsSortMetric == SortMetric.PlayCount,
        SeeAllCategory.Artists => ArtistsSortMetric == SortMetric.PlayCount,
        SeeAllCategory.Albums => AlbumsSortMetric == SortMetric.PlayCount,
        SeeAllCategory.Genres => GenresSortMetric == SortMetric.PlayCount,
        _ => true
    };
    public bool IsSeeAllByDuration => !IsSeeAllByPlayCount;

    public void SetCurrentCategoryMetric(SortMetric metric)
    {
        switch (CurrentSeeAllCategory)
        {
            case SeeAllCategory.Songs: SongsSortMetric = metric; break;
            case SeeAllCategory.Artists: ArtistsSortMetric = metric; break;
            case SeeAllCategory.Albums: AlbumsSortMetric = metric; break;
            case SeeAllCategory.Genres: GenresSortMetric = metric; break;
        }
    }

    async partial void OnSongsSortMetricChanged(SortMetric value)
    {
        if (IsSeeAllOpen && IsSeeAllSongs)
            await OpenSeeAllAsync(SeeAllCategory.Songs);
        await ReloadTopSongsAsync();
    }

    async partial void OnArtistsSortMetricChanged(SortMetric value)
    {
        if (IsSeeAllOpen && IsSeeAllArtists)
            await OpenSeeAllAsync(SeeAllCategory.Artists);
        await ReloadTopArtistsAsync();
    }

    async partial void OnAlbumsSortMetricChanged(SortMetric value)
    {
        if (IsSeeAllOpen && IsSeeAllAlbums)
            await OpenSeeAllAsync(SeeAllCategory.Albums);
        await ReloadTopAlbumsAsync();
    }

    async partial void OnGenresSortMetricChanged(SortMetric value)
    {
        if (IsSeeAllOpen && IsSeeAllGenres)
            await OpenSeeAllAsync(SeeAllCategory.Genres);
        await ReloadTopGenresAsync();
    }

    // ── Top lists ───────────────────────────────────────────────

    public ObservableRangeCollection<TopSongItem> TopSongs { get; } = new();

    public ObservableRangeCollection<TopArtistItem> TopArtists { get; } = new();

    public ObservableRangeCollection<TopAlbumItem> TopAlbums { get; } = new();

    public ObservableRangeCollection<TopGenreItem> TopGenres { get; } = new();

    // ── Playback source distribution ────────────────────────────

    [ObservableProperty]
    public partial ObservableRangeCollection<SourceSegment> SourceSegments { get; set; } = new();

    // ── "See All" overlay ────────────────────────────────────────

    private const int SeeAllPageSize = 25;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsSeeAllSongs),
        nameof(IsSeeAllArtists),
        nameof(IsSeeAllAlbums),
        nameof(IsSeeAllGenres),
        nameof(SeeAllSortLabel),
        nameof(IsSeeAllByPlayCount),
        nameof(IsSeeAllByDuration))]
    public partial SeeAllCategory CurrentSeeAllCategory { get; set; } = SeeAllCategory.None;

    [ObservableProperty] public partial bool IsSeeAllOpen { get; set; }
    [ObservableProperty] public partial string SeeAllTitle { get; set; } = "";
    [ObservableProperty] public partial bool IsSeeAllLoading { get; set; }

    [ObservableProperty] public partial string SearchTerm { get; set; } = string.Empty;

    async partial void OnSearchTermChanged(string value)
    {
        if (!IsSeeAllOpen) return;
        await _seeAllSearchDebouncer.RunAsync(token => _dispatcherService.EnqueueAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (IsSeeAllOpen)
                await OpenSeeAllAsync(CurrentSeeAllCategory);
        }));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeeAllNextPage), nameof(HasSeeAllPreviousPage))]
    public partial int SeeAllCurrentPage { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeeAllNextPage))]
    public partial int SeeAllTotalPages { get; set; } = 1;

    public bool HasSeeAllNextPage => SeeAllCurrentPage < SeeAllTotalPages;
    public bool HasSeeAllPreviousPage => SeeAllCurrentPage > 1;

    public bool IsSeeAllSongs => CurrentSeeAllCategory == SeeAllCategory.Songs;
    public bool IsSeeAllArtists => CurrentSeeAllCategory == SeeAllCategory.Artists;
    public bool IsSeeAllAlbums => CurrentSeeAllCategory == SeeAllCategory.Albums;
    public bool IsSeeAllGenres => CurrentSeeAllCategory == SeeAllCategory.Genres;

    public ObservableRangeCollection<TopSongItem> SeeAllSongs { get; } = new();
    public ObservableRangeCollection<TopArtistItem> SeeAllArtists { get; } = new();
    public ObservableRangeCollection<TopAlbumItem> SeeAllAlbums { get; } = new();
    public ObservableRangeCollection<TopGenreItem> SeeAllGenres { get; } = new();

    // ── Public API ──────────────────────────────────────────────

    /// <summary>
    ///     Loads all insights data for the currently selected time range.
    ///     Called on navigation and when the time range changes.
    /// </summary>
    [RelayCommand]
    public async Task LoadInsightsAsync()
    {
        var newCts = new CancellationTokenSource();
        var ct = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        Interlocked.Exchange(ref _songsSortCts, null)?.Cancel();
        Interlocked.Exchange(ref _artistsSortCts, null)?.Cancel();
        Interlocked.Exchange(ref _albumsSortCts, null)?.Cancel();
        Interlocked.Exchange(ref _genresSortCts, null)?.Cancel();

        IsLoading = true;
        HasData = false;

        try
        {
            var range = BuildTimeRange();

            // Fire independent queries in parallel.
            var totalTimeTask = _statisticsService.GetTotalListenTimeAsync(range, ct);
            var uniqueTask = _statisticsService.GetUniqueSongsPlayedAsync(range, ct);
            var listeningPatternsTask = _statisticsService.GetListeningPatternsAsync(range, ct);
            var topSongsTask = _statisticsService.GetTopSongsAsync(range, 10, metric: SongsSortMetric, ct: ct);
            var topArtistsTask = _statisticsService.GetTopArtistsAsync(range, 10, metric: ArtistsSortMetric, ct: ct);
            var topAlbumsTask = _statisticsService.GetTopAlbumsAsync(range, 10, metric: AlbumsSortMetric, ct: ct);
            var topGenresTask = _statisticsService.GetTopGenresAsync(range, 10, metric: GenresSortMetric, ct: ct);
            var sourcesTask = _statisticsService.GetPlaybackSourceDistributionAsync(range, ct);

            await Task.WhenAll(
                totalTimeTask, uniqueTask, listeningPatternsTask,
                topSongsTask, topArtistsTask, topAlbumsTask, topGenresTask,
                sourcesTask).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // Marshal results to UI thread.
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                // Hero stats
                TotalListenTimeText = FormatListenTime(totalTimeTask.Result);
                UniqueSongsPlayed = uniqueTask.Result;
                PeakHourText = FormatHour(listeningPatternsTask.Result.PeakHour);
                MostActiveDayText = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(listeningPatternsTask.Result.MostActiveDay);

                // Top songs
                TopSongs.ReplaceRange(topSongsTask.Result.Select(CreateSongItem));

                // Top artists
                TopArtists.ReplaceRange(topArtistsTask.Result.Select(CreateArtistItem));

                // Top albums
                TopAlbums.ReplaceRange(topAlbumsTask.Result.Select(CreateAlbumItem));

                // Top genres
                TopGenres.ReplaceRange(topGenresTask.Result.Select(CreateGenreItem));

                // Playback source distribution
                var sources = sourcesTask.Result.ToList();
                var totalCount = sources.Sum(s => s.Count);
                SourceSegments = new ObservableRangeCollection<SourceSegment>(totalCount > 0
                    ? sources
                        .OrderByDescending(s => s.Count)
                        .Select(s => new SourceSegment
                        {
                            Label = FormatContextType(s.Type),
                            Percentage = Math.Round(s.Count * 100.0 / totalCount, 1),
                            PercentageText = Math.Round(s.Count * 100.0 / totalCount, 1).ToString("F1", CultureInfo.CurrentCulture) + "%",
                            SegmentBrush = GetBrushForContextType(s.Type)
                        })
                    : Enumerable.Empty<SourceSegment>());

                HasData = UniqueSongsPlayed > 0 || TopSongs.Count > 0;

                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Insights load canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load listening insights");
            await _dispatcherService.EnqueueAsync(() => { if (!ct.IsCancellationRequested) HasData = false; return Task.CompletedTask; });
        }
        finally
        {
            await _dispatcherService.EnqueueAsync(() => { if (!ct.IsCancellationRequested) IsLoading = false; return Task.CompletedTask; });
        }
    }

    // ── Reset ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ResetListenHistoryAsync()
    {
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Strings.InsightsPage_Reset_Confirm_Title,
            Strings.InsightsPage_Reset_Confirm_Message,
            Strings.InsightsPage_Reset_Confirm_Button,
            null);

        if (!confirmed) return;

        try
        {
            await _libraryService.ClearListenHistoryAsync();
            _logger.LogInformation("Listen history reset by user.");
            await LoadInsightsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset listen history.");
        }
    }

    // ── See All ──────────────────────────────────────────────────

    [RelayCommand] private Task OpenSeeAllSongsAsync() => OpenSeeAllAsync(SeeAllCategory.Songs);
    [RelayCommand] private Task OpenSeeAllArtistsAsync() => OpenSeeAllAsync(SeeAllCategory.Artists);
    [RelayCommand] private Task OpenSeeAllAlbumsAsync() => OpenSeeAllAsync(SeeAllCategory.Albums);
    [RelayCommand] private Task OpenSeeAllGenresAsync() => OpenSeeAllAsync(SeeAllCategory.Genres);

    private async Task OpenSeeAllAsync(SeeAllCategory category, int page = 1, bool refreshTotalCount = true)
    {
        var newCts = new CancellationTokenSource();
        var ct = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _seeAllCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        var previousPage = refreshTotalCount ? 1 : SeeAllCurrentPage;

        try
        {
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                CurrentSeeAllCategory = category;
                SeeAllTitle = category switch
                {
                    SeeAllCategory.Songs => Strings.InsightsPage_TopSongs,
                    SeeAllCategory.Artists => Strings.InsightsPage_TopArtists,
                    SeeAllCategory.Albums => Strings.InsightsPage_TopAlbums,
                    SeeAllCategory.Genres => Strings.InsightsPage_TopGenres,
                    _ => ""
                };
                SeeAllCurrentPage = page;
                IsSeeAllOpen = true;
                IsSeeAllLoading = true;
                return Task.CompletedTask;
            });
            await LoadSeeAllPageAsync(category, page, ct, refreshTotalCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load see-all data for {Category}, page {Page}", category, page);
            await _dispatcherService.EnqueueAsync(() =>
            {
                if (!ct.IsCancellationRequested) SeeAllCurrentPage = previousPage;
                return Task.CompletedTask;
            });
        }
        finally
        {
            await _dispatcherService.EnqueueAsync(() =>
            {
                if (!ct.IsCancellationRequested) IsSeeAllLoading = false;
                return Task.CompletedTask;
            });
        }
    }

    private async Task LoadSeeAllPageAsync(
        SeeAllCategory category,
        int page,
        CancellationToken ct,
        bool refreshTotalCount = false)
    {
        var range = BuildTimeRange();
        var offset = (page - 1) * SeeAllPageSize;

        switch (category)
        {
            case SeeAllCategory.Songs:
                {
                    IEnumerable<SongStats> songs;
                    int? totalCount = null;
                    if (refreshTotalCount)
                    {
                        var result = await _statisticsService.GetTopSongsPageAsync(range, SeeAllPageSize, SongsSortMetric, offset, SearchTerm, ct);
                        songs = result.Items;
                        totalCount = result.TotalCount;
                    }
                    else
                    {
                        songs = await _statisticsService.GetTopSongsAsync(range, SeeAllPageSize, metric: SongsSortMetric, offset: offset, searchTerm: SearchTerm, ct: ct);
                    }
                    await _dispatcherService.EnqueueAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (totalCount.HasValue)
                            SeeAllTotalPages = Math.Max(1, (int)Math.Ceiling(totalCount.Value / (double)SeeAllPageSize));
                        SeeAllSongs.ReplaceRange(songs.Select(CreateSongItem));
                        return Task.CompletedTask;
                    });
                    break;
                }
            case SeeAllCategory.Artists:
                {
                    IEnumerable<ArtistStats> artists;
                    int? totalCount = null;
                    if (refreshTotalCount)
                    {
                        var result = await _statisticsService.GetTopArtistsPageAsync(range, SeeAllPageSize, ArtistsSortMetric, offset, SearchTerm, ct);
                        artists = result.Items;
                        totalCount = result.TotalCount;
                    }
                    else
                    {
                        artists = await _statisticsService.GetTopArtistsAsync(range, SeeAllPageSize, metric: ArtistsSortMetric, offset: offset, searchTerm: SearchTerm, ct: ct);
                    }
                    await _dispatcherService.EnqueueAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (totalCount.HasValue)
                            SeeAllTotalPages = Math.Max(1, (int)Math.Ceiling(totalCount.Value / (double)SeeAllPageSize));
                        SeeAllArtists.ReplaceRange(artists.Select(CreateArtistItem));
                        return Task.CompletedTask;
                    });
                    break;
                }
            case SeeAllCategory.Albums:
                {
                    IEnumerable<AlbumStats> albums;
                    int? totalCount = null;
                    if (refreshTotalCount)
                    {
                        var result = await _statisticsService.GetTopAlbumsPageAsync(range, SeeAllPageSize, AlbumsSortMetric, offset, SearchTerm, ct);
                        albums = result.Items;
                        totalCount = result.TotalCount;
                    }
                    else
                    {
                        albums = await _statisticsService.GetTopAlbumsAsync(range, SeeAllPageSize, metric: AlbumsSortMetric, offset: offset, searchTerm: SearchTerm, ct: ct);
                    }
                    await _dispatcherService.EnqueueAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (totalCount.HasValue)
                            SeeAllTotalPages = Math.Max(1, (int)Math.Ceiling(totalCount.Value / (double)SeeAllPageSize));
                        SeeAllAlbums.ReplaceRange(albums.Select(CreateAlbumItem));
                        return Task.CompletedTask;
                    });
                    break;
                }
            case SeeAllCategory.Genres:
                {
                    IEnumerable<GenreStats> genres;
                    int? totalCount = null;
                    if (refreshTotalCount)
                    {
                        var result = await _statisticsService.GetTopGenresPageAsync(range, SeeAllPageSize, GenresSortMetric, offset, SearchTerm, ct);
                        genres = result.Items;
                        totalCount = result.TotalCount;
                    }
                    else
                    {
                        genres = await _statisticsService.GetTopGenresAsync(range, SeeAllPageSize, metric: GenresSortMetric, offset: offset, searchTerm: SearchTerm, ct: ct);
                    }
                    await _dispatcherService.EnqueueAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (totalCount.HasValue)
                            SeeAllTotalPages = Math.Max(1, (int)Math.Ceiling(totalCount.Value / (double)SeeAllPageSize));
                        SeeAllGenres.ReplaceRange(genres.Select(CreateGenreItem));
                        return Task.CompletedTask;
                    });
                    break;
                }
        }
    }

    [RelayCommand]
    private async Task CloseSeeAll()
    {
        try { _seeAllCts?.Cancel(); } catch (ObjectDisposedException) { }
        _seeAllSearchDebouncer.Cancel();

        await _dispatcherService.EnqueueAsync(() =>
        {
            IsSeeAllOpen = false;
            SearchTerm = string.Empty;
            IsSeeAllLoading = false;
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    private Task SeeAllNextPage() => HasSeeAllNextPage
        ? OpenSeeAllAsync(CurrentSeeAllCategory, SeeAllCurrentPage + 1, refreshTotalCount: false)
        : Task.CompletedTask;

    [RelayCommand]
    private Task SeeAllPreviousPage() => HasSeeAllPreviousPage
        ? OpenSeeAllAsync(CurrentSeeAllCategory, SeeAllCurrentPage - 1, refreshTotalCount: false)
        : Task.CompletedTask;

    // ── Navigation ──────────────────────────────────────────────

    [RelayCommand]
    private void GoToArtist(TopArtistItem item) =>
        _navigationService.Navigate(typeof(ArtistViewPage), new ArtistViewNavigationParameter(item.ArtistId));

    [RelayCommand]
    private void GoToAlbum(TopAlbumItem item) =>
        _navigationService.Navigate(typeof(AlbumViewPage), new AlbumViewNavigationParameter(item.AlbumId));

    [RelayCommand]
    private void GoToGenre(TopGenreItem item) =>
        _navigationService.Navigate(typeof(GenreViewPage), new GenreViewNavigationParameter
        {
            GenreId = item.GenreId,
            GenreName = item.Name
        });

    // ── Per-card sort reloads ────────────────────────────────────

    private async Task ReloadTopSongsAsync()
    {
        var newCts = new CancellationTokenSource();
        var ct = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _songsSortCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        try
        {
            var range = BuildTimeRange();
            var songs = await _statisticsService.GetTopSongsAsync(range, 10, SongsSortMetric, ct: ct);
            ct.ThrowIfCancellationRequested();
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                TopSongs.ReplaceRange(songs.Select(CreateSongItem));
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Failed to reload top songs"); }
    }

    private async Task ReloadTopArtistsAsync()
    {
        var newCts = new CancellationTokenSource();
        var ct = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _artistsSortCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        try
        {
            var range = BuildTimeRange();
            var artists = await _statisticsService.GetTopArtistsAsync(range, 10, ArtistsSortMetric, ct: ct);
            ct.ThrowIfCancellationRequested();
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                TopArtists.ReplaceRange(artists.Select(CreateArtistItem));
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Failed to reload top artists"); }
    }

    private async Task ReloadTopAlbumsAsync()
    {
        var newCts = new CancellationTokenSource();
        var ct = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _albumsSortCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        try
        {
            var range = BuildTimeRange();
            var albums = await _statisticsService.GetTopAlbumsAsync(range, 10, AlbumsSortMetric, ct: ct);
            ct.ThrowIfCancellationRequested();
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                TopAlbums.ReplaceRange(albums.Select(CreateAlbumItem));
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Failed to reload top albums"); }
    }

    private async Task ReloadTopGenresAsync()
    {
        var newCts = new CancellationTokenSource();
        var ct = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _genresSortCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        try
        {
            var range = BuildTimeRange();
            var genres = await _statisticsService.GetTopGenresAsync(range, 10, GenresSortMetric, ct: ct);
            ct.ThrowIfCancellationRequested();
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                TopGenres.ReplaceRange(genres.Select(CreateGenreItem));
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Failed to reload top genres"); }
    }

    // ── Helpers ─────────────────────────────────────────────────

    private TopSongItem CreateSongItem(SongStats s) => new()
    {
        Rank = s.GlobalRank,
        Title = s.Song.Title,
        Artist = s.Song.ArtistName,
        ArtworkUri = ImageUriHelper.GetUriWithCacheBuster(s.Song.AlbumArtUriFromTrack),
        StatText = SongsSortMetric == SortMetric.PlayCount
            ? ResourceFormatter.Format(Strings.InsightsPage_Plays, s.TotalPlays)
            : FormatListenTime(s.TotalDuration),
    };

    private TopArtistItem CreateArtistItem(ArtistStats a) => new()
    {
        ArtistId = a.Artist.Id,
        Rank = a.GlobalRank,
        Name = a.Artist.Name,
        ImageUri = ImageUriHelper.GetUriWithCacheBuster(a.Artist.LocalImageCachePath ?? a.Artist.RemoteImageUrl),
        StatText = ArtistsSortMetric == SortMetric.PlayCount
            ? ResourceFormatter.Format(Strings.InsightsPage_Plays, a.TotalPlays)
            : FormatListenTime(a.TotalDuration),
        Command = GoToArtistCommand
    };

    private TopAlbumItem CreateAlbumItem(AlbumStats a) => new()
    {
        AlbumId = a.Album.Id,
        Rank = a.GlobalRank,
        Title = a.Album.Title,
        ArtistName = a.Album.ArtistName,
        ArtworkUri = ImageUriHelper.GetUriWithCacheBuster(a.Album.CoverArtUri),
        StatText = AlbumsSortMetric == SortMetric.PlayCount
            ? ResourceFormatter.Format(Strings.InsightsPage_Plays, a.TotalPlays)
            : FormatListenTime(a.TotalDuration),
        Command = GoToAlbumCommand
    };

    private TopGenreItem CreateGenreItem(GenreStats g) => new()
    {
        GenreId = g.Genre.Id,
        Rank = g.GlobalRank,
        Name = g.Genre.Name,
        StatText = GenresSortMetric == SortMetric.PlayCount
            ? ResourceFormatter.Format(Strings.InsightsPage_Plays, g.TotalPlays)
            : FormatListenTime(g.TotalDuration),
        Command = GoToGenreCommand
    };

    private TimeRange BuildTimeRange()
    {
        var now = DateTime.UtcNow;
        DateTime? start = SelectedTimeRangeIndex switch
        {
            0 => now.AddDays(-1),
            1 => now.AddDays(-7),
            2 => now.AddDays(-30),
            3 => now.AddDays(-90),
            4 => now.AddYears(-1),
            _ => null // All time
        };
        return new TimeRange(start, now);
    }

    private static string FormatListenTime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return string.Format(CultureInfo.CurrentCulture, Strings.Insights_Format_HoursMinutes, (int)ts.TotalHours, ts.Minutes);
        return string.Format(CultureInfo.CurrentCulture, Strings.Insights_Format_MinutesSeconds, ts.Minutes, ts.Seconds);
    }

    private static string FormatHour(int hour)
    {
        // Use 12-hour format with AM/PM for readability.
        var dt = new DateTime(2000, 1, 1, hour, 0, 0);
        return dt.ToString("h tt", CultureInfo.CurrentCulture);
    }

    private static readonly Dictionary<PlaybackContextType, Brush> _contextTypeBrushes = new()
    {
        [PlaybackContextType.Album] = new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68)),
        [PlaybackContextType.Artist] = new SolidColorBrush(ColorHelper.FromArgb(255, 249, 115, 22)),
        [PlaybackContextType.Playlist] = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),
        [PlaybackContextType.SmartPlaylist] = new SolidColorBrush(ColorHelper.FromArgb(255, 6, 182, 212)),
        [PlaybackContextType.Folder] = new SolidColorBrush(ColorHelper.FromArgb(255, 139, 92, 246)),
        [PlaybackContextType.Genre] = new SolidColorBrush(ColorHelper.FromArgb(255, 236, 72, 153)),
        [PlaybackContextType.Search] = new SolidColorBrush(ColorHelper.FromArgb(255, 99, 102, 241)),
        [PlaybackContextType.Library] = new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11)),
        [PlaybackContextType.Transient] = new SolidColorBrush(ColorHelper.FromArgb(255, 107, 114, 128)),
    };

    private static readonly Brush _defaultContextBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 156, 163, 175));

    private static Brush GetBrushForContextType(PlaybackContextType type)
    {
        return _contextTypeBrushes.TryGetValue(type, out var brush) ? brush : _defaultContextBrush;
    }

    private static string FormatContextType(PlaybackContextType type) => type switch
    {
        PlaybackContextType.Album => Strings.InsightsPage_Source_Albums,
        PlaybackContextType.Artist => Strings.InsightsPage_Source_Artists,
        PlaybackContextType.Playlist => Strings.InsightsPage_Source_Playlists,
        PlaybackContextType.SmartPlaylist => Strings.InsightsPage_Source_SmartPlaylists,
        PlaybackContextType.Folder => Strings.InsightsPage_Source_Folders,
        PlaybackContextType.Genre => Strings.InsightsPage_Source_Genres,
        PlaybackContextType.Search => Strings.InsightsPage_Source_Search,
        PlaybackContextType.Library => Strings.InsightsPage_Source_Library,
        PlaybackContextType.Transient => Strings.InsightsPage_Source_Files,
        _ => type.ToString()
    };
}
