using Nagi.Core.Library;
using Nagi.Core.Playback;

namespace Nagi.Core.Services.Abstractions;

public interface IStatisticsService
{
    /// <summary>
    ///     Gets one page of the ranking and how many items match <paramref name="searchTerm" />. Ranks are positions in
    ///     the whole ranking, so a search does not renumber the items it keeps.
    /// </summary>
    Task<StatisticsPage<SongStats>> GetTopSongsAsync(TimeRange range, SortMetric metric, int offset, int limit, string? searchTerm = null, CancellationToken ct = default);

    /// <inheritdoc cref="GetTopSongsAsync" />
    Task<StatisticsPage<ArtistStats>> GetTopArtistsAsync(TimeRange range, SortMetric metric, int offset, int limit, string? searchTerm = null, CancellationToken ct = default);

    /// <inheritdoc cref="GetTopSongsAsync" />
    Task<StatisticsPage<AlbumStats>> GetTopAlbumsAsync(TimeRange range, SortMetric metric, int offset, int limit, string? searchTerm = null, CancellationToken ct = default);

    /// <inheritdoc cref="GetTopSongsAsync" />
    Task<StatisticsPage<GenreStats>> GetTopGenresAsync(TimeRange range, SortMetric metric, int offset, int limit, string? searchTerm = null, CancellationToken ct = default);

    Task<TimeSpan> GetTotalListenTimeAsync(TimeRange range, CancellationToken ct = default);

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

public record SongStats(Song Song, int TotalPlays, TimeSpan TotalDuration, int GlobalRank);
public record ArtistStats(Artist Artist, int TotalPlays, TimeSpan TotalDuration, int GlobalRank);
public record AlbumStats(Album Album, int TotalPlays, TimeSpan TotalDuration, int GlobalRank);
public record GenreStats(Genre Genre, int TotalPlays, TimeSpan TotalDuration, int GlobalRank);
public record ContextStats(PlaybackContextType Type, int Count, TimeSpan Duration);
public record ListeningPatternStats(DayOfWeek MostActiveDay, int PeakHour);
public record StatisticsPage<T>(IReadOnlyList<T> Items, int TotalCount);
