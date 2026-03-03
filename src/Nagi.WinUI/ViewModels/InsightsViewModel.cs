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
public partial class SourceSegment : ObservableObject
{
    public string Label { get; init; } = string.Empty;
    public double Percentage { get; init; }
    public string PercentageText { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public int Count { get; init; }
    public Brush SegmentBrush { get; init; } = new SolidColorBrush(Colors.Gray);
}

/// <summary>
///     A display-friendly wrapper for top-song statistics shown in the insights cards.
/// </summary>
public partial class TopSongItem : ObservableObject
{
    public int Rank { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string? ArtworkUri { get; init; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(ArtworkUri);
    public int PlayCount { get; init; }
    public string PlayCountText { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public int Skips { get; init; }
}

/// <summary>
///     A display-friendly wrapper for top-artist statistics.
/// </summary>
public partial class TopArtistItem : ObservableObject
{
    public Guid ArtistId { get; init; }
    public int Rank { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? ImageUri { get; init; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(ImageUri);
    public int PlayCount { get; init; }
    public TimeSpan Duration { get; init; }
    public IRelayCommand<TopArtistItem>? Command { get; init; }
}

/// <summary>
///     A display-friendly wrapper for top-album statistics.
/// </summary>
public partial class TopAlbumItem : ObservableObject
{
    public Guid AlbumId { get; init; }
    public int Rank { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ArtistName { get; init; } = string.Empty;
    public string? ArtworkUri { get; init; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(ArtworkUri);
    public int PlayCount { get; init; }
    public string PlayCountText { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public IRelayCommand<TopAlbumItem>? Command { get; init; }
}

/// <summary>
///     A display-friendly wrapper for top-genre statistics.
/// </summary>
public partial class TopGenreItem : ObservableObject
{
    public Guid GenreId { get; init; }
    public int Rank { get; init; }
    public string Name { get; init; } = string.Empty;
    public int PlayCount { get; init; }
    public string PlayCountText { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
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
    private CancellationTokenSource? _seeAllSearchDebounceCts;
    private CancellationTokenSource? _seeAllCts;
    private CancellationTokenSource? _loadCts;

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
        new[] { "Last 1 day", "Last 7 days", "Last 30 days", "Last 90 days", "Last year", "All time" };

    [ObservableProperty] public partial int SelectedTimeRangeIndex { get; set; } = 2; // default 30D

    async partial void OnSelectedTimeRangeIndexChanged(int value)
    {
        if (IsSeeAllOpen)
            CloseSeeAll();
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

    // ── Top lists ───────────────────────────────────────────────

    [ObservableProperty]
    public partial ObservableRangeCollection<TopSongItem> TopSongs { get; set; } = new();

    [ObservableProperty]
    public partial ObservableRangeCollection<TopArtistItem> TopArtists { get; set; } = new();

    [ObservableProperty]
    public partial ObservableRangeCollection<TopAlbumItem> TopAlbums { get; set; } = new();

    [ObservableProperty]
    public partial ObservableRangeCollection<TopGenreItem> TopGenres { get; set; } = new();

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
        nameof(IsSeeAllGenres))]
    public partial SeeAllCategory CurrentSeeAllCategory { get; set; } = SeeAllCategory.None;

    [ObservableProperty] public partial bool IsSeeAllOpen { get; set; }
    [ObservableProperty] public partial string SeeAllTitle { get; set; } = "";
    [ObservableProperty] public partial bool IsSeeAllLoading { get; set; }

    [ObservableProperty] public partial string SearchTerm { get; set; } = string.Empty;

    partial void OnSearchTermChanged(string value)
    {
        _seeAllSearchDebounceCts?.Cancel();
        _seeAllSearchDebounceCts?.Dispose();
        _seeAllSearchDebounceCts = new CancellationTokenSource();
        var token = _seeAllSearchDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                if (token.IsCancellationRequested) return;

                if (IsSeeAllOpen)
                {
                    await OpenSeeAllAsync(CurrentSeeAllCategory);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
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

    // Paged display collections bound to the overlay ListViews.
    [ObservableProperty] public partial ObservableRangeCollection<TopSongItem> SeeAllSongs { get; set; } = new();
    [ObservableProperty] public partial ObservableRangeCollection<TopArtistItem> SeeAllArtists { get; set; } = new();
    [ObservableProperty] public partial ObservableRangeCollection<TopAlbumItem> SeeAllAlbums { get; set; } = new();
    [ObservableProperty] public partial ObservableRangeCollection<TopGenreItem> SeeAllGenres { get; set; } = new();

    // ── Public API ──────────────────────────────────────────────

    /// <summary>
    ///     Loads all insights data for the currently selected time range.
    ///     Called on navigation and when the time range changes.
    /// </summary>
    [RelayCommand]
    public async Task LoadInsightsAsync()
    {
        // Cancel any in-flight load.
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch (ObjectDisposedException) { }

        var ct = newCts.Token;

        IsLoading = true;
        HasData = false;

        try
        {
            var range = BuildTimeRange();

            // Fire independent queries in parallel.
            var totalTimeTask = _statisticsService.GetTotalListenTimeAsync(range, ct);
            var uniqueTask = _statisticsService.GetUniqueSongsPlayedAsync(range, ct);
            var peakHourTask = _statisticsService.GetPeakListeningHourAsync(range, ct);
            var activeDayTask = _statisticsService.GetMostActiveDayOfWeekAsync(range, ct);
            var topSongsTask = _statisticsService.GetTopSongsAsync(range, 10, metric: SortMetric.PlayCount, ct: ct);
            var topArtistsTask = _statisticsService.GetTopArtistsAsync(range, 10, metric: SortMetric.Duration, ct: ct);
            var topAlbumsTask = _statisticsService.GetTopAlbumsAsync(range, 10, ct: ct);
            var topGenresTask = _statisticsService.GetTopGenresAsync(range, 10, ct: ct);
            var sourcesTask = _statisticsService.GetPlaybackSourceDistributionAsync(range, ct);

            await Task.WhenAll(
                totalTimeTask, uniqueTask, peakHourTask, activeDayTask,
                topSongsTask, topArtistsTask, topAlbumsTask, topGenresTask,
                sourcesTask).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // Marshal results to UI thread.
            await _dispatcherService.EnqueueAsync(() =>
            {
                // Hero stats
                TotalListenTimeText = FormatListenTime(totalTimeTask.Result);
                UniqueSongsPlayed = uniqueTask.Result;
                PeakHourText = FormatHour(peakHourTask.Result);
                MostActiveDayText = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(activeDayTask.Result);

                // Top songs
                TopSongs.ReplaceRange(topSongsTask.Result.Select((s, i) => new TopSongItem
                {
                    Rank = i + 1,
                    Title = s.Song.Title,
                    Artist = string.Join(", ", s.Song.SongArtists?.Select(sa => sa.Artist?.Name ?? "") ?? []),
                    ArtworkUri = ImageUriHelper.GetUriWithCacheBuster(s.Song.AlbumArtUriFromTrack),
                    PlayCount = s.TotalPlays,
                    PlayCountText = string.Format(CultureInfo.CurrentCulture, Strings.InsightsPage_Plays, s.TotalPlays),
                    Duration = s.TotalDuration,
                    Skips = s.Skips
                }));

                // Top artists
                TopArtists.ReplaceRange(topArtistsTask.Result.Select((a, i) => new TopArtistItem
                {
                    ArtistId = a.Artist.Id,
                    Rank = i + 1,
                    Name = a.Artist.Name,
                    ImageUri = ImageUriHelper.GetUriWithCacheBuster(a.Artist.LocalImageCachePath ?? a.Artist.RemoteImageUrl),
                    PlayCount = a.TotalPlays,
                    Duration = a.TotalDuration,
                    Command = GoToArtistCommand
                }));

                // Top albums
                TopAlbums.ReplaceRange(topAlbumsTask.Result.Select((a, i) => new TopAlbumItem
                {
                    AlbumId = a.Album.Id,
                    Rank = i + 1,
                    Title = a.Album.Title,
                    ArtistName = a.Album.ArtistName ?? "",
                    ArtworkUri = ImageUriHelper.GetUriWithCacheBuster(a.Album.CoverArtUri),
                    PlayCount = a.TotalPlays,
                    PlayCountText = string.Format(CultureInfo.CurrentCulture, Strings.InsightsPage_Plays, a.TotalPlays),
                    Duration = a.TotalDuration,
                    Command = GoToAlbumCommand
                }));

                // Top genres
                TopGenres.ReplaceRange(topGenresTask.Result.Select((g, i) => new TopGenreItem
                {
                    GenreId = g.Genre.Id,
                    Rank = i + 1,
                    Name = g.Genre.Name,
                    PlayCount = g.TotalPlays,
                    PlayCountText = string.Format(CultureInfo.CurrentCulture, Strings.InsightsPage_Plays, g.TotalPlays),
                    Duration = g.TotalDuration,
                    Command = GoToGenreCommand
                }));

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
                            Duration = s.Duration,
                            Count = s.Count,
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
            await _dispatcherService.EnqueueAsync(() => { HasData = false; return Task.CompletedTask; });
        }
        finally
        {
            await _dispatcherService.EnqueueAsync(() => { IsLoading = false; return Task.CompletedTask; });
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
            await _libraryService.ClearListenHistoryAsync().ConfigureAwait(false);
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

    private async Task OpenSeeAllAsync(SeeAllCategory category)
    {
        // Cancel any in-flight see-all load.
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _seeAllCts, newCts);
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch (ObjectDisposedException) { }

        var ct = newCts.Token;

        await _dispatcherService.EnqueueAsync(() =>
        {
            CurrentSeeAllCategory = category;
            SeeAllTitle = category switch
            {
                SeeAllCategory.Songs => Strings.InsightsPage_TopSongs,
                SeeAllCategory.Artists => Strings.InsightsPage_TopArtists,
                SeeAllCategory.Albums => Strings.InsightsPage_TopAlbums,
                SeeAllCategory.Genres => Strings.InsightsPage_TopGenres,
                _ => ""
            };
            SeeAllCurrentPage = 1;
            IsSeeAllOpen = true;
            IsSeeAllLoading = true;
            return Task.CompletedTask;
        });

        try
        {
            var range = BuildTimeRange();

            // Fetch total count from DB so we can compute pages without loading all data.
            int totalCount = category switch
            {
                SeeAllCategory.Songs => await _statisticsService.GetTopSongsCountAsync(range, SearchTerm, ct),
                SeeAllCategory.Artists => await _statisticsService.GetTopArtistsCountAsync(range, SearchTerm, ct),
                SeeAllCategory.Albums => await _statisticsService.GetTopAlbumsCountAsync(range, SearchTerm, ct),
                SeeAllCategory.Genres => await _statisticsService.GetTopGenresCountAsync(range, SearchTerm, ct),
                _ => 0
            };

            await _dispatcherService.EnqueueAsync(() =>
            {
                SeeAllTotalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)SeeAllPageSize));
                return Task.CompletedTask;
            });

            await LoadSeeAllPageAsync(category, 1, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("SeeAll load canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load see-all data for {Category}", category);
        }
        finally
        {
            await _dispatcherService.EnqueueAsync(() =>
            {
                IsSeeAllLoading = false;
                return Task.CompletedTask;
            });
        }
    }

    private async Task LoadSeeAllPageAsync(SeeAllCategory category, int page, CancellationToken ct)
    {
        var range = BuildTimeRange();
        var offset = (page - 1) * SeeAllPageSize;

        switch (category)
        {
            case SeeAllCategory.Songs:
            {
                var songs = await _statisticsService.GetTopSongsAsync(range, SeeAllPageSize, metric: SortMetric.PlayCount, offset: offset, searchTerm: SearchTerm, ct: ct);
                await _dispatcherService.EnqueueAsync(() =>
                {
                    SeeAllSongs.ReplaceRange(songs.Select((s, i) => new TopSongItem
                    {
                        Rank = offset + i + 1,
                        Title = s.Song.Title,
                        Artist = string.Join(", ", s.Song.SongArtists?.Select(sa => sa.Artist?.Name ?? "") ?? []),
                        ArtworkUri = ImageUriHelper.GetUriWithCacheBuster(s.Song.AlbumArtUriFromTrack),
                        PlayCount = s.TotalPlays,
                        PlayCountText = string.Format(CultureInfo.CurrentCulture, Strings.InsightsPage_Plays, s.TotalPlays),
                        Duration = s.TotalDuration,
                        Skips = s.Skips
                    }));
                    return Task.CompletedTask;
                });
                break;
            }
            case SeeAllCategory.Artists:
            {
                var artists = await _statisticsService.GetTopArtistsAsync(range, SeeAllPageSize, metric: SortMetric.Duration, offset: offset, searchTerm: SearchTerm, ct: ct);
                await _dispatcherService.EnqueueAsync(() =>
                {
                    SeeAllArtists.ReplaceRange(artists.Select((a, i) => new TopArtistItem
                    {
                        ArtistId = a.Artist.Id,
                        Rank = offset + i + 1,
                        Name = a.Artist.Name,
                        ImageUri = ImageUriHelper.GetUriWithCacheBuster(a.Artist.LocalImageCachePath ?? a.Artist.RemoteImageUrl),
                        PlayCount = a.TotalPlays,
                        Duration = a.TotalDuration,
                        Command = GoToArtistCommand
                    }));
                    return Task.CompletedTask;
                });
                break;
            }
            case SeeAllCategory.Albums:
            {
                var albums = await _statisticsService.GetTopAlbumsAsync(range, SeeAllPageSize, offset: offset, searchTerm: SearchTerm, ct: ct);
                await _dispatcherService.EnqueueAsync(() =>
                {
                    SeeAllAlbums.ReplaceRange(albums.Select((a, i) => new TopAlbumItem
                    {
                        AlbumId = a.Album.Id,
                        Rank = offset + i + 1,
                        Title = a.Album.Title,
                        ArtistName = a.Album.ArtistName ?? "",
                        ArtworkUri = ImageUriHelper.GetUriWithCacheBuster(a.Album.CoverArtUri),
                        PlayCount = a.TotalPlays,
                        PlayCountText = string.Format(CultureInfo.CurrentCulture, Strings.InsightsPage_Plays, a.TotalPlays),
                        Duration = a.TotalDuration,
                        Command = GoToAlbumCommand
                    }));
                    return Task.CompletedTask;
                });
                break;
            }
            case SeeAllCategory.Genres:
            {
                var genres = await _statisticsService.GetTopGenresAsync(range, SeeAllPageSize, offset: offset, searchTerm: SearchTerm, ct: ct);
                await _dispatcherService.EnqueueAsync(() =>
                {
                    SeeAllGenres.ReplaceRange(genres.Select((g, i) => new TopGenreItem
                    {
                        GenreId = g.Genre.Id,
                        Rank = offset + i + 1,
                        Name = g.Genre.Name,
                        PlayCount = g.TotalPlays,
                        PlayCountText = string.Format(CultureInfo.CurrentCulture, Strings.InsightsPage_Plays, g.TotalPlays),
                        Duration = g.TotalDuration,
                        Command = GoToGenreCommand
                    }));
                    return Task.CompletedTask;
                });
                break;
            }
        }
    }

    [RelayCommand]
    private async void CloseSeeAll()
    {
        try { _seeAllCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _seeAllSearchDebounceCts?.Cancel(); } catch (ObjectDisposedException) { }
        
        await _dispatcherService.EnqueueAsync(() =>
        {
            SearchTerm = string.Empty;
            IsSeeAllOpen = false;
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    private async Task SeeAllNextPage()
    {
        if (!HasSeeAllNextPage) return;
        
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _seeAllCts, newCts);
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch (ObjectDisposedException) { }
        
        int previousPage = SeeAllCurrentPage;
        await _dispatcherService.EnqueueAsync(() =>
        {
            SeeAllCurrentPage++;
            IsSeeAllLoading = true;
            return Task.CompletedTask;
        });

        try 
        { 
            await LoadSeeAllPageAsync(CurrentSeeAllCategory, SeeAllCurrentPage, newCts.Token); 
        }
        catch (OperationCanceledException) 
        {
            _logger.LogDebug("SeeAll next page canceled.");
            await _dispatcherService.EnqueueAsync(() => { SeeAllCurrentPage = previousPage; return Task.CompletedTask; });
        }
        catch (Exception ex) 
        { 
            _logger.LogError(ex, "Failed to load see-all page {Page}", SeeAllCurrentPage); 
            await _dispatcherService.EnqueueAsync(() => { SeeAllCurrentPage = previousPage; return Task.CompletedTask; });
        }
        finally
        {
            await _dispatcherService.EnqueueAsync(() => { IsSeeAllLoading = false; return Task.CompletedTask; });
        }
    }

    [RelayCommand]
    private async Task SeeAllPreviousPage()
    {
        if (!HasSeeAllPreviousPage) return;

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _seeAllCts, newCts);
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch (ObjectDisposedException) { }
        
        int previousPage = SeeAllCurrentPage;
        await _dispatcherService.EnqueueAsync(() =>
        {
            SeeAllCurrentPage--;
            IsSeeAllLoading = true;
            return Task.CompletedTask;
        });

        try 
        { 
            await LoadSeeAllPageAsync(CurrentSeeAllCategory, SeeAllCurrentPage, newCts.Token); 
        }
        catch (OperationCanceledException) 
        {
            _logger.LogDebug("SeeAll previous page canceled.");
            await _dispatcherService.EnqueueAsync(() => { SeeAllCurrentPage = previousPage; return Task.CompletedTask; });
        }
        catch (Exception ex) 
        { 
            _logger.LogError(ex, "Failed to load see-all page {Page}", SeeAllCurrentPage); 
            await _dispatcherService.EnqueueAsync(() => { SeeAllCurrentPage = previousPage; return Task.CompletedTask; });
        }
        finally
        {
            await _dispatcherService.EnqueueAsync(() => { IsSeeAllLoading = false; return Task.CompletedTask; });
        }
    }

    // ── Navigation ──────────────────────────────────────────────

    [RelayCommand]
    private void GoToArtist(TopArtistItem item) =>
        _navigationService.Navigate(typeof(ArtistViewPage), new ArtistViewNavigationParameter
        {
            ArtistId = item.ArtistId,
            ArtistName = item.Name
        });

    [RelayCommand]
    private void GoToAlbum(TopAlbumItem item) =>
        _navigationService.Navigate(typeof(AlbumViewPage), new AlbumViewNavigationParameter
        {
            AlbumId = item.AlbumId,
            AlbumTitle = item.Title,
            ArtistName = item.ArtistName
        });

    [RelayCommand]
    private void GoToGenre(TopGenreItem item) =>
        _navigationService.Navigate(typeof(GenreViewPage), new GenreViewNavigationParameter
        {
            GenreId = item.GenreId,
            GenreName = item.Name
        });

    // ── Helpers ─────────────────────────────────────────────────

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
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }

    private static string FormatHour(int hour)
    {
        // Use 12-hour format with AM/PM for readability.
        var dt = new DateTime(2000, 1, 1, hour, 0, 0);
        return dt.ToString("h tt", CultureInfo.CurrentCulture);
    }

    private static Brush GetBrushForContextType(PlaybackContextType type)
    {
        var color = type switch
        {
            PlaybackContextType.Album => ColorHelper.FromArgb(255, 239, 68, 68), // Red
            PlaybackContextType.Artist => ColorHelper.FromArgb(255, 249, 115, 22), // Orange
            PlaybackContextType.Playlist => ColorHelper.FromArgb(255, 16, 185, 129), // Emerald
            PlaybackContextType.SmartPlaylist => ColorHelper.FromArgb(255, 6, 182, 212), // Cyan
            PlaybackContextType.Folder => ColorHelper.FromArgb(255, 139, 92, 246), // Violet
            PlaybackContextType.Genre => ColorHelper.FromArgb(255, 236, 72, 153), // Pink
            PlaybackContextType.Search => ColorHelper.FromArgb(255, 99, 102, 241), // Indigo
            PlaybackContextType.Queue => ColorHelper.FromArgb(255, 245, 158, 11), // Amber
            PlaybackContextType.Transient => ColorHelper.FromArgb(255, 107, 114, 128), // Gray
            _ => ColorHelper.FromArgb(255, 156, 163, 175)
        };
        return new SolidColorBrush(color);
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
        PlaybackContextType.Queue => Strings.InsightsPage_Source_Queue,
        PlaybackContextType.Transient => Strings.InsightsPage_Source_Files,
        _ => type.ToString()
    };
}
