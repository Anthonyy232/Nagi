using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     One segment of the playback source bar.
/// </summary>
public sealed class SourceSegment
{
    public string Label { get; init; } = string.Empty;
    public double Percentage { get; init; }
    public string PercentageText { get; init; } = string.Empty;
    public PlaybackContextType ContextType { get; init; }

    public override string ToString() => Label;
}

public abstract class InsightsRankedItem
{
    public int Rank { get; init; }
    public string StatText { get; init; } = string.Empty;
}

public sealed class TopSongItem : InsightsRankedItem
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string? ArtworkUri { get; init; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(ArtworkUri);

    public override string ToString() => $"{Title} — {Artist}";
}

public sealed class TopArtistItem : InsightsRankedItem
{
    public Guid ArtistId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? ImageUri { get; init; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(ImageUri);
    public IRelayCommand<TopArtistItem>? Command { get; init; }

    public override string ToString() => Name;
}

public sealed class TopAlbumItem : InsightsRankedItem
{
    public Guid AlbumId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ArtistName { get; init; } = string.Empty;
    public string? ArtworkUri { get; init; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(ArtworkUri);
    public IRelayCommand<TopAlbumItem>? Command { get; init; }

    public override string ToString() => $"{Title} — {ArtistName}";
}

public sealed class TopGenreItem : InsightsRankedItem
{
    public Guid GenreId { get; init; }
    public string Name { get; init; } = string.Empty;
    public IRelayCommand<TopGenreItem>? Command { get; init; }

    public override string ToString() => Name;
}

/// <param name="Start">The start of the range for a given end, or null for all time.</param>
public sealed record InsightsTimeRange(string Label, Func<DateTime, DateTime>? Start);

public partial class InsightsViewModel : ObservableObject
{
    private const int TopCount = 10;
    private const int SeeAllPageSize = 25;

    private static readonly InsightsTimeRange Last30Days = new(Strings.Insights_TimeRange_Last30Days, now => now.AddDays(-30));

    private readonly IStatisticsService _statisticsService;
    private readonly IDispatcherService _dispatcherService;
    private readonly INavigationService _navigationService;
    private readonly IListeningService _listening;
    private readonly IUIService _uiService;
    private readonly ILogger<InsightsViewModel> _logger;
    private readonly Debouncer _seeAllSearchDebouncer = new(TimeSpan.FromMilliseconds(300));
    private CancellationTokenSource? _seeAllCts;
    private CancellationTokenSource? _loadCts;
    private InsightsTimeRange _selectedTimeRange = Last30Days;

    public InsightsViewModel(
        IStatisticsService statisticsService,
        IDispatcherService dispatcherService,
        INavigationService navigationService,
        IListeningService listening,
        IUIService uiService,
        ILogger<InsightsViewModel> logger)
    {
        _statisticsService = statisticsService;
        _dispatcherService = dispatcherService;
        _navigationService = navigationService;
        _listening = listening;
        _uiService = uiService;
        _logger = logger;

        Songs = new InsightsCategoryViewModel(this, Strings.InsightsPage_TopSongs, SortMetric.PlayCount,
            async (range, metric, offset, limit, searchTerm, ct) => ToItems(
                await _statisticsService.GetTopSongsAsync(range, metric, offset, limit, searchTerm, ct),
                song => new TopSongItem
                {
                    Rank = song.GlobalRank,
                    Title = song.Song.Title,
                    Artist = song.Song.ArtistName,
                    ArtworkUri = ImageUriHelper.GetUriWithCacheBuster(song.Song.AlbumArtUriFromTrack),
                    StatText = FormatStat(metric, song.TotalPlays, song.TotalDuration)
                }));
        Artists = new InsightsCategoryViewModel(this, Strings.InsightsPage_TopArtists, SortMetric.Duration,
            async (range, metric, offset, limit, searchTerm, ct) => ToItems(
                await _statisticsService.GetTopArtistsAsync(range, metric, offset, limit, searchTerm, ct),
                artist => new TopArtistItem
                {
                    ArtistId = artist.Artist.Id,
                    Rank = artist.GlobalRank,
                    Name = artist.Artist.Name,
                    ImageUri = ImageUriHelper.GetUriWithCacheBuster(artist.Artist.LocalImageCachePath),
                    StatText = FormatStat(metric, artist.TotalPlays, artist.TotalDuration),
                    Command = GoToArtistCommand
                }));
        Albums = new InsightsCategoryViewModel(this, Strings.InsightsPage_TopAlbums, SortMetric.PlayCount,
            async (range, metric, offset, limit, searchTerm, ct) => ToItems(
                await _statisticsService.GetTopAlbumsAsync(range, metric, offset, limit, searchTerm, ct),
                album => new TopAlbumItem
                {
                    AlbumId = album.Album.Id,
                    Rank = album.GlobalRank,
                    Title = album.Album.Title,
                    ArtistName = album.Album.ArtistName,
                    ArtworkUri = ImageUriHelper.GetUriWithCacheBuster(album.Album.CoverArtUri),
                    StatText = FormatStat(metric, album.TotalPlays, album.TotalDuration),
                    Command = GoToAlbumCommand
                }));
        Genres = new InsightsCategoryViewModel(this, Strings.InsightsPage_TopGenres, SortMetric.PlayCount,
            async (range, metric, offset, limit, searchTerm, ct) => ToItems(
                await _statisticsService.GetTopGenresAsync(range, metric, offset, limit, searchTerm, ct),
                genre => new TopGenreItem
                {
                    GenreId = genre.Genre.Id,
                    Rank = genre.GlobalRank,
                    Name = genre.Genre.Name,
                    StatText = FormatStat(metric, genre.TotalPlays, genre.TotalDuration),
                    Command = GoToGenreCommand
                }));
        Categories = [Songs, Artists, Albums, Genres];
    }

    public static IReadOnlyList<InsightsTimeRange> TimeRanges { get; } =
    [
        new(Strings.Insights_TimeRange_Last1Day, now => now.AddDays(-1)),
        new(Strings.Insights_TimeRange_Last7Days, now => now.AddDays(-7)),
        Last30Days,
        new(Strings.Insights_TimeRange_Last90Days, now => now.AddDays(-90)),
        new(Strings.Insights_TimeRange_LastYear, now => now.AddYears(-1)),
        new(Strings.Insights_TimeRange_AllTime, null)
    ];

    public InsightsTimeRange SelectedTimeRange
    {
        get => _selectedTimeRange;
        set
        {
            // ComboBox can clear its selection while it unloads.
            if (value is not null && SetProperty(ref _selectedTimeRange, value))
                _ = ReloadForTimeRangeAsync();
        }
    }

    private async Task ReloadForTimeRangeAsync()
    {
        if (IsSeeAllOpen)
            await CloseSeeAll();
        await LoadInsightsAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool HasData { get; set; }

    public bool ShowEmptyState => !IsLoading && !HasData;

    [ObservableProperty] public partial string TotalListenTimeText { get; set; } = "—";
    [ObservableProperty] public partial int UniqueSongsPlayed { get; set; }
    [ObservableProperty] public partial string PeakHourText { get; set; } = "—";
    [ObservableProperty] public partial string MostActiveDayText { get; set; } = "—";

    public InsightsCategoryViewModel Songs { get; }
    public InsightsCategoryViewModel Artists { get; }
    public InsightsCategoryViewModel Albums { get; }
    public InsightsCategoryViewModel Genres { get; }

    private IReadOnlyList<InsightsCategoryViewModel> Categories { get; }

    [ObservableProperty]
    public partial ObservableRangeCollection<SourceSegment> SourceSegments { get; set; } = new();

    /// <summary>
    ///     The category shown in the "See all" overlay, or null while the overlay is closed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSeeAllOpen))]
    public partial InsightsCategoryViewModel? SeeAllCategory { get; set; }

    public bool IsSeeAllOpen => SeeAllCategory is not null;

    [ObservableProperty] public partial bool IsSeeAllLoading { get; set; }

    [ObservableProperty] public partial string SearchTerm { get; set; } = string.Empty;

    async partial void OnSearchTermChanged(string value)
    {
        if (!IsSeeAllOpen) return;
        await _seeAllSearchDebouncer.RunAsync(token => _dispatcherService.EnqueueAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (SeeAllCategory is { } category)
                await OpenSeeAllAsync(category);
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

    public ObservableRangeCollection<InsightsRankedItem> SeeAllItems { get; } = new();

    /// <summary>
    ///     Loads all insights data for the selected time range.
    /// </summary>
    public async Task LoadInsightsAsync()
    {
        var newCts = new CancellationTokenSource();
        var ct = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        foreach (var category in Categories) category.CancelReload();

        IsLoading = true;
        HasData = false;

        try
        {
            var range = BuildTimeRange();

            var totalTimeTask = _statisticsService.GetTotalListenTimeAsync(range, ct);
            var listeningPatternsTask = _statisticsService.GetListeningPatternsAsync(range, ct);
            var topItemTasks = Categories.ToDictionary(category => category,
                category => category.LoadAsync(range, 0, TopCount, null, ct));
            var sourcesTask = _statisticsService.GetPlaybackSourceDistributionAsync(range, ct);

            await Task.WhenAll(topItemTasks.Values.Append<Task>(totalTimeTask).Append(listeningPatternsTask).Append(sourcesTask))
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                TotalListenTimeText = FormatListenTime(totalTimeTask.Result);
                UniqueSongsPlayed = topItemTasks[Songs].Result.TotalCount;
                PeakHourText = FormatHour(listeningPatternsTask.Result.PeakHour);
                MostActiveDayText = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(listeningPatternsTask.Result.MostActiveDay);

                foreach (var (category, items) in topItemTasks)
                    category.TopItems.ReplaceRange(items.Result.Items);

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
                            ContextType = s.Type
                        })
                    : Enumerable.Empty<SourceSegment>());

                HasData = UniqueSongsPlayed > 0 || Songs.TopItems.Count > 0;

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
            await _listening.ClearListenHistoryAsync();
            _logger.LogInformation("Listen history reset by user.");
            await LoadInsightsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset listen history.");
        }
    }

    internal Task OpenSeeAllAsync(InsightsCategoryViewModel category) => ShowSeeAllPageAsync(category, 1, 1);

    internal async Task OnMetricChangedAsync(InsightsCategoryViewModel category)
    {
        if (SeeAllCategory == category)
            await OpenSeeAllAsync(category);
        await ReloadTopItemsAsync(category);
    }

    private async Task ShowSeeAllPageAsync(InsightsCategoryViewModel category, int page, int previousPage)
    {
        var newCts = new CancellationTokenSource();
        var ct = newCts.Token;
        var oldCts = Interlocked.Exchange(ref _seeAllCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();

        try
        {
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                SeeAllCategory = category;
                SeeAllCurrentPage = page;
                IsSeeAllLoading = true;
                return Task.CompletedTask;
            });

            var result = await category.LoadAsync(BuildTimeRange(), (page - 1) * SeeAllPageSize, SeeAllPageSize, SearchTerm, ct);
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                SeeAllTotalPages = Math.Max(1, (int)Math.Ceiling(result.TotalCount / (double)SeeAllPageSize));
                SeeAllItems.ReplaceRange(result.Items);
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load see-all data for {Category}, page {Page}", category.Title, page);
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

    [RelayCommand]
    private async Task CloseSeeAll()
    {
        _seeAllCts?.Cancel();
        _seeAllSearchDebouncer.Cancel();

        await _dispatcherService.EnqueueAsync(() =>
        {
            SeeAllCategory = null;
            SearchTerm = string.Empty;
            IsSeeAllLoading = false;
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    private Task SeeAllNextPage() => HasSeeAllNextPage && SeeAllCategory is { } category
        ? ShowSeeAllPageAsync(category, SeeAllCurrentPage + 1, SeeAllCurrentPage)
        : Task.CompletedTask;

    [RelayCommand]
    private Task SeeAllPreviousPage() => HasSeeAllPreviousPage && SeeAllCategory is { } category
        ? ShowSeeAllPageAsync(category, SeeAllCurrentPage - 1, SeeAllCurrentPage)
        : Task.CompletedTask;

    [RelayCommand]
    private void GoToArtist(TopArtistItem item) =>
        _navigationService.NavigateTo(new ArtistViewNavigationParameter(item.ArtistId));

    [RelayCommand]
    private void GoToAlbum(TopAlbumItem item) =>
        _navigationService.NavigateTo(new AlbumViewNavigationParameter(item.AlbumId));

    [RelayCommand]
    private void GoToGenre(TopGenreItem item) =>
        _navigationService.NavigateTo(new GenreViewNavigationParameter
        {
            GenreId = item.GenreId,
            GenreName = item.Name
        });

    private async Task ReloadTopItemsAsync(InsightsCategoryViewModel category)
    {
        var ct = category.RestartReload();
        try
        {
            var result = await category.LoadAsync(BuildTimeRange(), 0, TopCount, null, ct);
            ct.ThrowIfCancellationRequested();
            await _dispatcherService.EnqueueAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                category.TopItems.ReplaceRange(result.Items);
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Failed to reload {Category}", category.Title); }
    }

    private static StatisticsPage<InsightsRankedItem> ToItems<TStats>(StatisticsPage<TStats> page, Func<TStats, InsightsRankedItem> create) =>
        new(page.Items.Select(create).ToList(), page.TotalCount);

    private TimeRange BuildTimeRange()
    {
        var now = DateTime.UtcNow;
        return new TimeRange(SelectedTimeRange.Start?.Invoke(now), now);
    }

    private static string FormatStat(SortMetric metric, int plays, TimeSpan duration) =>
        metric == SortMetric.PlayCount
            ? ResourceFormatter.Format(Strings.InsightsPage_Plays, plays)
            : FormatListenTime(duration);

    private static string FormatListenTime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return ResourceFormatter.Format(Strings.Insights_Format_HoursMinutes, (int)ts.TotalHours, ts.Minutes);
        return ResourceFormatter.Format(Strings.Insights_Format_MinutesSeconds, ts.Minutes, ts.Seconds);
    }

    private static string FormatHour(int hour)
    {
        var dt = new DateTime(2000, 1, 1, hour, 0, 0);
        return dt.ToString("h tt", CultureInfo.CurrentCulture);
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
