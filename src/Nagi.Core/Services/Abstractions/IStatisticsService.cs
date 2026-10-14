using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

public interface IStatisticsService
{
    Task<IEnumerable<SongStats>> GetTopSongsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.PlayCount, int offset = 0, string? searchTerm = null, CancellationToken ct = default);

    /// <summary>
    ///     Gets one page and the matching item count in a single search scan.
    /// </summary>
    Task<StatisticsPage<SongStats>> GetTopSongsPageAsync(TimeRange range, int limit, SortMetric metric, int offset, string? searchTerm, CancellationToken ct = default);

    Task<IEnumerable<ArtistStats>> GetTopArtistsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.Duration, int offset = 0, string? searchTerm = null, CancellationToken ct = default);

    Task<StatisticsPage<ArtistStats>> GetTopArtistsPageAsync(TimeRange range, int limit, SortMetric metric, int offset, string? searchTerm, CancellationToken ct = default);

    Task<IEnumerable<AlbumStats>> GetTopAlbumsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.PlayCount, int offset = 0, string? searchTerm = null, CancellationToken ct = default);

    Task<StatisticsPage<AlbumStats>> GetTopAlbumsPageAsync(TimeRange range, int limit, SortMetric metric, int offset, string? searchTerm, CancellationToken ct = default);

    Task<IEnumerable<GenreStats>> GetTopGenresAsync(TimeRange range, int limit = 10, SortMetric metric = SortMetric.PlayCount, int offset = 0, string? searchTerm = null, CancellationToken ct = default);

    Task<StatisticsPage<GenreStats>> GetTopGenresPageAsync(TimeRange range, int limit, SortMetric metric, int offset, string? searchTerm, CancellationToken ct = default);

    Task<TimeSpan> GetTotalListenTimeAsync(TimeRange range, CancellationToken ct = default);

    Task<int> GetUniqueSongsPlayedAsync(TimeRange range, CancellationToken ct = default);

    /// <summary>
    ///     Gets the most active local day and peak local hour from one history scan.
    /// </summary>
    Task<ListeningPatternStats> GetListeningPatternsAsync(TimeRange range, CancellationToken ct = default);

    Task<IEnumerable<ContextStats>> GetPlaybackSourceDistributionAsync(TimeRange range, CancellationToken ct = default);
}

public record TimeRange(DateTime? StartUtc, DateTime? EndUtc);

public enum SortMetric
{
    PlayCount,

    Duration
}

public record SongStats(Song Song, int TotalPlays, TimeSpan TotalDuration, int Skips, int GlobalRank = 0);
public record ArtistStats(Artist Artist, int TotalPlays, TimeSpan TotalDuration, int GlobalRank = 0);
public record AlbumStats(Album Album, int TotalPlays, TimeSpan TotalDuration, int GlobalRank = 0);
public record GenreStats(Genre Genre, int TotalPlays, TimeSpan TotalDuration, int GlobalRank = 0);
public record ContextStats(PlaybackContextType Type, int Count, TimeSpan Duration);
public record ListeningPatternStats(DayOfWeek MostActiveDay, int PeakHour);
public record StatisticsPage<T>(IReadOnlyList<T> Items, int TotalCount);
