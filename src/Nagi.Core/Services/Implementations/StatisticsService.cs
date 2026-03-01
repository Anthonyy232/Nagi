using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

public class StatisticsService : IStatisticsService
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;

    public StatisticsService(IDbContextFactory<MusicDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SongStats>> GetTopSongsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.PlayCount)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        var statsQuery = query.GroupBy(lh => lh.SongId)
            .Select(g => new
            {
                SongId = g.Key,
                TotalPlays = g.Count(lh => lh.IsEligibleForScrobbling),
                TotalDurationTicks = g.Sum(lh => lh.ListenDurationTicks),
                Skips = g.Count(lh => lh.EndReason == PlaybackEndReason.Skipped && !lh.IsEligibleForScrobbling)
            });

        if (metric == SortMetric.PlayCount)
            statsQuery = statsQuery.OrderByDescending(s => s.TotalPlays);
        else
            statsQuery = statsQuery.OrderByDescending(s => s.TotalDurationTicks);

        var topItems = await statsQuery.Take(limit).ToListAsync().ConfigureAwait(false);
        var songIds = topItems.Select(ti => ti.SongId).ToList();
        var songs = await dbContext.Songs.AsNoTracking()
            .Where(s => songIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id).ConfigureAwait(false);

        return topItems
            .Where(ti => songs.ContainsKey(ti.SongId))
            .Select(ti => new SongStats(
                songs[ti.SongId],
                ti.TotalPlays,
                TimeSpan.FromTicks(ti.TotalDurationTicks),
                ti.Skips
            ));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ArtistStats>> GetTopArtistsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.Duration)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        // This is simplified and might need adjustment for multi-artist tracks
        var statsQuery = query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .SelectMany(x => x.s.SongArtists, (x, sa) => new { x.lh, sa.ArtistId })
            .GroupBy(x => x.ArtistId)
            .Select(g => new
            {
                ArtistId = g.Key,
                TotalPlays = g.Count(x => x.lh.IsEligibleForScrobbling),
                TotalDurationTicks = g.Sum(x => x.lh.ListenDurationTicks)
            });

        if (metric == SortMetric.PlayCount)
            statsQuery = statsQuery.OrderByDescending(s => s.TotalPlays);
        else
            statsQuery = statsQuery.OrderByDescending(s => s.TotalDurationTicks);

        var topItems = await statsQuery.Take(limit).ToListAsync().ConfigureAwait(false);
        var artistIds = topItems.Select(ti => ti.ArtistId).ToList();
        var artists = await dbContext.Artists.AsNoTracking()
            .Where(a => artistIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id).ConfigureAwait(false);

        return topItems
            .Where(ti => artists.ContainsKey(ti.ArtistId))
            .Select(ti => new ArtistStats(
                artists[ti.ArtistId],
                ti.TotalPlays,
                TimeSpan.FromTicks(ti.TotalDurationTicks)
            ));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AlbumStats>> GetTopAlbumsAsync(TimeRange range, int limit = 50)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        var statsQuery = query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .GroupBy(x => x.s.AlbumId)
            .Select(g => new
            {
                AlbumId = g.Key,
                TotalPlays = g.Count(x => x.lh.IsEligibleForScrobbling),
                TotalDurationTicks = g.Sum(x => x.lh.ListenDurationTicks)
            })
            .OrderByDescending(s => s.TotalPlays);

        var topItems = await statsQuery.Take(limit).ToListAsync().ConfigureAwait(false);
        var albumIds = topItems.Select(ti => ti.AlbumId).Where(id => id.HasValue).Cast<Guid>().ToList();
        var albums = await dbContext.Albums.AsNoTracking()
            .Where(a => albumIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id).ConfigureAwait(false);

        return topItems
            .Where(ti => ti.AlbumId.HasValue && albums.ContainsKey(ti.AlbumId.Value))
            .Select(ti => new AlbumStats(
                albums[ti.AlbumId!.Value],
                ti.TotalPlays,
                TimeSpan.FromTicks(ti.TotalDurationTicks)
            ));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<GenreStats>> GetTopGenresAsync(TimeRange range, int limit = 10)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        var statsQuery = query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .SelectMany(x => x.s.Genres, (x, g) => new { x.lh, g.Id })
            .GroupBy(x => x.Id)
            .Select(g => new
            {
                GenreId = g.Key,
                TotalPlays = g.Count(x => x.lh.IsEligibleForScrobbling),
                TotalDurationTicks = g.Sum(x => x.lh.ListenDurationTicks)
            })
            .OrderByDescending(s => s.TotalPlays);

        var topItems = await statsQuery.Take(limit).ToListAsync().ConfigureAwait(false);
        var genreIds = topItems.Select(ti => ti.GenreId).ToList();
        var genres = await dbContext.Genres.AsNoTracking()
            .Where(g => genreIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id).ConfigureAwait(false);

        return topItems
            .Where(ti => genres.ContainsKey(ti.GenreId))
            .Select(ti => new GenreStats(
                genres[ti.GenreId],
                ti.TotalPlays,
                TimeSpan.FromTicks(ti.TotalDurationTicks)
            ));
    }

    /// <inheritdoc />
    public async Task<TimeSpan> GetTotalListenTimeAsync(TimeRange range)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        var totalTicks = await query.SumAsync(lh => lh.ListenDurationTicks).ConfigureAwait(false);
        return TimeSpan.FromTicks(totalTicks);
    }

    /// <inheritdoc />
    public async Task<int> GetUniqueSongsPlayedAsync(TimeRange range)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        return await query.Where(lh => lh.IsEligibleForScrobbling).Select(lh => lh.SongId).Distinct().CountAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IEnumerable<ActivityDataPoint>> GetListeningActivityTimelineAsync(TimeRange range, ActivityInterval interval)
    {
        // TODO: Requires SQLite strftime grouping by interval (Hour/Day/Week/Month).
        throw new NotImplementedException("GetListeningActivityTimelineAsync is not yet implemented.");
    }

    /// <inheritdoc />
    public async Task<DayOfWeek> GetMostActiveDayOfWeekAsync(TimeRange range)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        // Group client-side because SQLite strftime is not translatable via LINQ.
        // For very large tables a raw-SQL approach would be faster, but this avoids
        // parameterised-NULL pitfalls with FormattableString and keeps the code testable.
        var timestamps = await query
            .Select(lh => lh.ListenTimestampUtc)
            .ToListAsync()
            .ConfigureAwait(false);

        if (timestamps.Count == 0) return DayOfWeek.Monday;

        return timestamps
            .GroupBy(t => t.DayOfWeek)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;
    }

    /// <inheritdoc />
    public async Task<int> GetPeakListeningHourAsync(TimeRange range)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        var timestamps = await query
            .Select(lh => lh.ListenTimestampUtc)
            .ToListAsync()
            .ConfigureAwait(false);

        if (timestamps.Count == 0) return 12;

        return timestamps
            .GroupBy(t => t.Hour)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ContextStats>> GetPlaybackSourceDistributionAsync(TimeRange range)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        var stats = await query.GroupBy(lh => lh.ContextType)
            .Select(g => new
            {
                Type = g.Key,
                Count = g.Count(),
                DurationTicks = g.Sum(lh => lh.ListenDurationTicks)
            }).ToListAsync().ConfigureAwait(false);

        return stats.Select(s => new ContextStats(
            s.Type,
            s.Count,
            TimeSpan.FromTicks(s.DurationTicks)
        ));
    }
}
