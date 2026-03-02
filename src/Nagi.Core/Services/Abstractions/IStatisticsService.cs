using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Provides methods for aggregating and querying listening statistics and insights.
/// </summary>
public interface IStatisticsService
{
    // --- Top Item Queries ---

    /// <summary>
    ///     Gets the top songs within a specific time range.
    /// </summary>
    Task<IEnumerable<SongStats>> GetTopSongsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.PlayCount, CancellationToken ct = default);

    /// <summary>
    ///     Gets the top artists within a specific time range.
    /// </summary>
    Task<IEnumerable<ArtistStats>> GetTopArtistsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.Duration, CancellationToken ct = default);

    /// <summary>
    ///     Gets the top albums within a specific time range.
    /// </summary>
    Task<IEnumerable<AlbumStats>> GetTopAlbumsAsync(TimeRange range, int limit = 50, CancellationToken ct = default);

    /// <summary>
    ///     Gets the top genres within a specific time range.
    /// </summary>
    Task<IEnumerable<GenreStats>> GetTopGenresAsync(TimeRange range, int limit = 10, CancellationToken ct = default);

    // --- Aggregate Queries ---

    /// <summary>
    ///     Gets the total duration of music listened to within a time range.
    /// </summary>
    Task<TimeSpan> GetTotalListenTimeAsync(TimeRange range, CancellationToken ct = default);

    /// <summary>
    ///     Gets the count of unique songs played within a time range.
    /// </summary>
    Task<int> GetUniqueSongsPlayedAsync(TimeRange range, CancellationToken ct = default);

    /// <summary>
    ///     Gets a timeline of listening activity over a specified range.
    /// </summary>
    Task<IEnumerable<ActivityDataPoint>> GetListeningActivityTimelineAsync(TimeRange range, ActivityInterval interval, CancellationToken ct = default);

    // --- Habit Queries ---

    /// <summary>
    ///     Identifies the day of the week with the most listening activity.
    /// </summary>
    Task<DayOfWeek> GetMostActiveDayOfWeekAsync(TimeRange range, CancellationToken ct = default);

    /// <summary>
    ///     Identifies the hour of the day (0-23) with the peak listening activity.
    /// </summary>
    Task<int> GetPeakListeningHourAsync(TimeRange range, CancellationToken ct = default);

    /// <summary>
    ///     Gets the distribution of playback sources (Album, Playlist, etc.) used.
    /// </summary>
    Task<IEnumerable<ContextStats>> GetPlaybackSourceDistributionAsync(TimeRange range, CancellationToken ct = default);
}

// Support Models for Statistics

public record TimeRange(DateTime? StartUtc, DateTime? EndUtc);

public enum SortMetric 
{ 
    /// <summary>
    ///     Sort by the number of times a track was marked as played.
    /// </summary>
    PlayCount, 

    /// <summary>
    ///     Sort by the total cumulative duration of listening.
    /// </summary>
    Duration 
}

public enum ActivityInterval 
{ 
    Hour, 
    Day, 
    Week, 
    Month 
}

public record SongStats(Song Song, int TotalPlays, TimeSpan TotalDuration, int Skips);
public record ArtistStats(Artist Artist, int TotalPlays, TimeSpan TotalDuration);
public record AlbumStats(Album Album, int TotalPlays, TimeSpan TotalDuration);
public record GenreStats(Genre Genre, int TotalPlays, TimeSpan TotalDuration);
public record ActivityDataPoint(DateTime Timestamp, int Plays, TimeSpan Duration);
public record ContextStats(PlaybackContextType Type, int Count, TimeSpan Duration);
