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
    public async Task<IEnumerable<SongStats>> GetTopSongsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.PlayCount, int offset = 0, string? searchTerm = null, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(lh => dbContext.Songs.Any(s => s.Id == lh.SongId && s.Title.ToLower().Contains(searchTerm.ToLower())));
        }

        var statsQuery = query.GroupBy(lh => lh.SongId)
            .Select(g => new
            {
                SongId = g.Key,
                TotalPlays = g.Sum(lh => lh.IsEligibleForScrobbling || lh.EndReason == PlaybackEndReason.Finished ? 1 : 0),
                TotalDurationTicks = g.Sum(lh => lh.ListenDurationTicks),
                Skips = g.Sum(lh => lh.EndReason == PlaybackEndReason.Skipped && !lh.IsEligibleForScrobbling ? 1 : 0)
            })
            .Where(s => s.TotalPlays > 0);

        if (metric == SortMetric.PlayCount)
            statsQuery = statsQuery.OrderByDescending(s => s.TotalPlays).ThenBy(s => s.SongId);
        else
            statsQuery = statsQuery.OrderByDescending(s => s.TotalDurationTicks).ThenBy(s => s.SongId);

        var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        var songIds = topItems.Select(ti => ti.SongId).ToList();
        var songs = await dbContext.Songs.AsNoTracking()
            .Where(s => songIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);

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
    public async Task<int> GetTopSongsCountAsync(TimeRange range, string? searchTerm = null, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(lh => dbContext.Songs.Any(s => s.Id == lh.SongId && s.Title.ToLower().Contains(searchTerm.ToLower())));
        }

        return await query
            .GroupBy(lh => lh.SongId)
            .Select(g => new { TotalPlays = g.Sum(lh => lh.IsEligibleForScrobbling || lh.EndReason == PlaybackEndReason.Finished ? 1 : 0) })
            .Where(s => s.TotalPlays > 0)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ArtistStats>> GetTopArtistsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.Duration, int offset = 0, string? searchTerm = null, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(lh => dbContext.Songs.Any(s => s.Id == lh.SongId && s.SongArtists.Any(sa => sa.Artist!.Name.ToLower().Contains(searchTerm.ToLower()))));
        }

        // This is simplified and might need adjustment for multi-artist tracks
        var statsQuery = query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .SelectMany(x => x.s.SongArtists, (x, sa) => new { x.lh, sa.ArtistId })
            .GroupBy(x => x.ArtistId)
            .Select(g => new
            {
                ArtistId = g.Key,
                TotalPlays = g.Sum(x => x.lh.IsEligibleForScrobbling || x.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0),
                TotalDurationTicks = g.Sum(x => x.lh.ListenDurationTicks)
            })
            .Where(s => s.TotalPlays > 0);

        if (metric == SortMetric.PlayCount)
            statsQuery = statsQuery.OrderByDescending(s => s.TotalPlays).ThenBy(s => s.ArtistId);
        else
            statsQuery = statsQuery.OrderByDescending(s => s.TotalDurationTicks).ThenBy(s => s.ArtistId);

        var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        var artistIds = topItems.Select(ti => ti.ArtistId).ToList();
        var artists = await dbContext.Artists.AsNoTracking()
            .Where(a => artistIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);

        return topItems
            .Where(ti => artists.ContainsKey(ti.ArtistId))
            .Select(ti => new ArtistStats(
                artists[ti.ArtistId],
                ti.TotalPlays,
                TimeSpan.FromTicks(ti.TotalDurationTicks)
            ));
    }

    /// <inheritdoc />
    public async Task<int> GetTopArtistsCountAsync(TimeRange range, string? searchTerm = null, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(lh => dbContext.Songs.Any(s => s.Id == lh.SongId && s.SongArtists.Any(sa => sa.Artist!.Name.ToLower().Contains(searchTerm.ToLower()))));
        }

        return await query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .SelectMany(x => x.s.SongArtists, (x, sa) => new { x.lh, sa.ArtistId })
            .GroupBy(x => x.ArtistId)
            .Select(g => new { TotalPlays = g.Sum(x => x.lh.IsEligibleForScrobbling || x.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0) })
            .Where(s => s.TotalPlays > 0)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AlbumStats>> GetTopAlbumsAsync(TimeRange range, int limit = 50, int offset = 0, string? searchTerm = null, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(lh => dbContext.Songs.Any(s => s.Id == lh.SongId && s.Album!.Title.ToLower().Contains(searchTerm.ToLower())));
        }

        var statsQuery = query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .GroupBy(x => x.s.AlbumId)
            .Select(g => new
            {
                AlbumId = g.Key,
                TotalPlays = g.Sum(x => x.lh.IsEligibleForScrobbling || x.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0),
                TotalDurationTicks = g.Sum(x => x.lh.ListenDurationTicks)
            })
            .Where(s => s.TotalPlays > 0)
            .OrderByDescending(s => s.TotalPlays).ThenBy(s => s.AlbumId);

        var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        var albumIds = topItems.Select(ti => ti.AlbumId).Where(id => id.HasValue).Cast<Guid>().ToList();
        var albums = await dbContext.Albums.AsNoTracking()
            .Where(a => albumIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);

        return topItems
            .Where(ti => ti.AlbumId.HasValue && albums.ContainsKey(ti.AlbumId.Value))
            .Select(ti => new AlbumStats(
                albums[ti.AlbumId!.Value],
                ti.TotalPlays,
                TimeSpan.FromTicks(ti.TotalDurationTicks)
            ));
    }

    /// <inheritdoc />
    public async Task<int> GetTopAlbumsCountAsync(TimeRange range, string? searchTerm = null, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(lh => dbContext.Songs.Any(s => s.Id == lh.SongId && s.Album!.Title.ToLower().Contains(searchTerm.ToLower())));
        }

        return await query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .GroupBy(x => x.s.AlbumId)
            .Select(g => new
            {
                AlbumId = g.Key,
                TotalPlays = g.Sum(x => x.lh.IsEligibleForScrobbling || x.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0)
            })
            .Where(s => s.TotalPlays > 0 && s.AlbumId != null)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<GenreStats>> GetTopGenresAsync(TimeRange range, int limit = 10, int offset = 0, string? searchTerm = null, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(lh => dbContext.Songs.Any(s => s.Id == lh.SongId && s.Genres.Any(g => g.Name.ToLower().Contains(searchTerm.ToLower()))));
        }

        var statsQuery = query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .SelectMany(x => x.s.Genres, (x, g) => new { x.lh, g.Id })
            .GroupBy(x => x.Id)
            .Select(g => new
            {
                GenreId = g.Key,
                TotalPlays = g.Sum(x => x.lh.IsEligibleForScrobbling || x.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0),
                TotalDurationTicks = g.Sum(x => x.lh.ListenDurationTicks)
            })
            .Where(s => s.TotalPlays > 0)
            .OrderByDescending(s => s.TotalPlays).ThenBy(s => s.GenreId);

        var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        var genreIds = topItems.Select(ti => ti.GenreId).ToList();
        var genres = await dbContext.Genres.AsNoTracking()
            .Where(g => genreIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct).ConfigureAwait(false);

        return topItems
            .Where(ti => genres.ContainsKey(ti.GenreId))
            .Select(ti => new GenreStats(
                genres[ti.GenreId],
                ti.TotalPlays,
                TimeSpan.FromTicks(ti.TotalDurationTicks)
            ));
    }

    /// <inheritdoc />
    public async Task<int> GetTopGenresCountAsync(TimeRange range, string? searchTerm = null, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(lh => dbContext.Songs.Any(s => s.Id == lh.SongId && s.Genres.Any(g => g.Name.ToLower().Contains(searchTerm.ToLower()))));
        }

        return await query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .SelectMany(x => x.s.Genres, (x, g) => new { x.lh, g.Id })
            .GroupBy(x => x.Id)
            .Select(g => new { TotalPlays = g.Sum(x => x.lh.IsEligibleForScrobbling || x.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0) })
            .Where(s => s.TotalPlays > 0)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TimeSpan> GetTotalListenTimeAsync(TimeRange range, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        var totalTicks = await query.SumAsync(lh => lh.ListenDurationTicks, ct).ConfigureAwait(false);
        return TimeSpan.FromTicks(totalTicks);
    }

    /// <inheritdoc />
    public async Task<int> GetUniqueSongsPlayedAsync(TimeRange range, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        return await query
            .Where(lh => lh.IsEligibleForScrobbling || lh.EndReason == PlaybackEndReason.Finished)
            .Select(lh => lh.SongId)
            .Distinct()
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IEnumerable<ActivityDataPoint>> GetListeningActivityTimelineAsync(TimeRange range, ActivityInterval interval, CancellationToken ct = default)
    {
        // TODO: Requires SQLite strftime grouping by interval (Hour/Day/Week/Month).
        throw new NotImplementedException("GetListeningActivityTimelineAsync is not yet implemented.");
    }

    /// <inheritdoc />
    public async Task<DayOfWeek> GetMostActiveDayOfWeekAsync(TimeRange range, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (timestamps.Count == 0) return DayOfWeek.Monday;

        return timestamps
            .GroupBy(t => t.ToLocalTime().DayOfWeek)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;
    }

    /// <inheritdoc />
    public async Task<int> GetPeakListeningHourAsync(TimeRange range, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        var timestamps = await query
            .Select(lh => lh.ListenTimestampUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (timestamps.Count == 0) return 12;

        return timestamps
            .GroupBy(t => t.ToLocalTime().Hour)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ContextStats>> GetPlaybackSourceDistributionAsync(TimeRange range, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
            }).ToListAsync(ct).ConfigureAwait(false);

        return stats.Select(s => new ContextStats(
            s.Type,
            s.Count,
            TimeSpan.FromTicks(s.DurationTicks)
        ));
    }
}
