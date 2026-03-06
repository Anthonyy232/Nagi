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

        // Aggregate WITHOUT search filter so global ranks are based on the full dataset.
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

        List<(int GlobalRank, Guid SongId, int TotalPlays, long TotalDurationTicks, int Skips)> page;

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            // No search: DB handles pagination. Global rank = offset + position + 1.
            var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
            page = topItems.Select((s, i) => (offset + i + 1, s.SongId, s.TotalPlays, s.TotalDurationTicks, s.Skips)).ToList();
        }
        else
        {
            // Search active: fetch all ranked stats, filter by title in-memory, then paginate.
            var allStats = await statsQuery.ToListAsync(ct).ConfigureAwait(false);
            var allIds = allStats.Select(s => s.SongId).ToHashSet();
            var titleMap = await dbContext.Songs.AsNoTracking()
                .Where(s => allIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Title })
                .ToDictionaryAsync(s => s.Id, s => s.Title, ct).ConfigureAwait(false);

            page = allStats
                .Select((s, i) => (GlobalRank: i + 1, s.SongId, s.TotalPlays, s.TotalDurationTicks, s.Skips))
                .Where(x => titleMap.TryGetValue(x.SongId, out var t) && t.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                .Skip(offset).Take(limit)
                .ToList();
        }

        var songIds = page.Select(x => x.SongId).ToHashSet();
        var songs = await dbContext.Songs.AsNoTracking()
            .Where(s => songIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);

        return page
            .Where(x => songs.ContainsKey(x.SongId))
            .Select(x => new SongStats(songs[x.SongId], x.TotalPlays, TimeSpan.FromTicks(x.TotalDurationTicks), x.Skips, x.GlobalRank));
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

        var statsQuery = query
            .GroupBy(lh => lh.SongId)
            .Select(g => new { SongId = g.Key, TotalPlays = g.Sum(lh => lh.IsEligibleForScrobbling || lh.EndReason == PlaybackEndReason.Finished ? 1 : 0) })
            .Where(s => s.TotalPlays > 0);

        if (string.IsNullOrWhiteSpace(searchTerm))
            return await statsQuery.CountAsync(ct).ConfigureAwait(false);

        var allStats = await statsQuery.ToListAsync(ct).ConfigureAwait(false);
        var allIds = allStats.Select(s => s.SongId).ToHashSet();
        var titleMap = await dbContext.Songs.AsNoTracking()
            .Where(s => allIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title })
            .ToDictionaryAsync(s => s.Id, s => s.Title, ct).ConfigureAwait(false);

        return allStats.Count(s => titleMap.TryGetValue(s.SongId, out var t) && t.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
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

        // Aggregate WITHOUT search filter so global ranks are based on the full dataset.
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

        List<(int GlobalRank, Guid ArtistId, int TotalPlays, long TotalDurationTicks)> page;

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            // No search: DB handles pagination. Global rank = offset + position + 1.
            var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
            page = topItems.Select((s, i) => (offset + i + 1, s.ArtistId, s.TotalPlays, s.TotalDurationTicks)).ToList();
        }
        else
        {
            // Search active: fetch all ranked stats, filter by artist name in-memory, then paginate.
            var allStats = await statsQuery.ToListAsync(ct).ConfigureAwait(false);
            var allIds = allStats.Select(s => s.ArtistId).ToHashSet();
            var nameMap = await dbContext.Artists.AsNoTracking()
                .Where(a => allIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Name })
                .ToDictionaryAsync(a => a.Id, a => a.Name, ct).ConfigureAwait(false);

            page = allStats
                .Select((s, i) => (GlobalRank: i + 1, s.ArtistId, s.TotalPlays, s.TotalDurationTicks))
                .Where(x => nameMap.TryGetValue(x.ArtistId, out var n) && n.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                .Skip(offset).Take(limit)
                .ToList();
        }

        var artistIds = page.Select(x => x.ArtistId).ToHashSet();
        var artists = await dbContext.Artists.AsNoTracking()
            .Where(a => artistIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);

        return page
            .Where(x => artists.ContainsKey(x.ArtistId))
            .Select(x => new ArtistStats(artists[x.ArtistId], x.TotalPlays, TimeSpan.FromTicks(x.TotalDurationTicks), x.GlobalRank));
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

        var statsQuery = query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .SelectMany(x => x.s.SongArtists, (x, sa) => new { x.lh, sa.ArtistId })
            .GroupBy(x => x.ArtistId)
            .Select(g => new { ArtistId = g.Key, TotalPlays = g.Sum(x => x.lh.IsEligibleForScrobbling || x.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0) })
            .Where(s => s.TotalPlays > 0);

        if (string.IsNullOrWhiteSpace(searchTerm))
            return await statsQuery.CountAsync(ct).ConfigureAwait(false);

        var allStats = await statsQuery.ToListAsync(ct).ConfigureAwait(false);
        var allIds = allStats.Select(s => s.ArtistId).ToHashSet();
        var nameMap = await dbContext.Artists.AsNoTracking()
            .Where(a => allIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name })
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct).ConfigureAwait(false);

        return allStats.Count(s => nameMap.TryGetValue(s.ArtistId, out var n) && n.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
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

        // Aggregate WITHOUT search filter so global ranks are based on the full dataset.
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

        List<(int GlobalRank, Guid AlbumId, int TotalPlays, long TotalDurationTicks)> page;

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            // No search: DB handles pagination. Global rank = offset + position + 1.
            // Assign rank before filtering null AlbumIds so ranks reflect true global position.
            var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
            page = topItems
                .Select((s, i) => (GlobalRank: offset + i + 1, s.AlbumId, s.TotalPlays, s.TotalDurationTicks))
                .Where(x => x.AlbumId.HasValue)
                .Select(x => (x.GlobalRank, AlbumId: x.AlbumId!.Value, x.TotalPlays, x.TotalDurationTicks))
                .ToList();
        }
        else
        {
            // Search active: fetch all ranked stats, filter by album title in-memory, then paginate.
            // Assign rank before filtering null AlbumIds so ranks reflect true global position.
            var allStats = await statsQuery.ToListAsync(ct).ConfigureAwait(false);
            var allIds = allStats.Where(s => s.AlbumId.HasValue).Select(s => s.AlbumId!.Value).ToHashSet();
            var titleMap = await dbContext.Albums.AsNoTracking()
                .Where(a => allIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Title })
                .ToDictionaryAsync(a => a.Id, a => a.Title, ct).ConfigureAwait(false);

            page = allStats
                .Select((s, i) => (GlobalRank: i + 1, s.AlbumId, s.TotalPlays, s.TotalDurationTicks))
                .Where(x => x.AlbumId.HasValue)
                .Select(x => (x.GlobalRank, AlbumId: x.AlbumId!.Value, x.TotalPlays, x.TotalDurationTicks))
                .Where(x => titleMap.TryGetValue(x.AlbumId, out var t) && t.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                .Skip(offset).Take(limit)
                .ToList();
        }

        var albumIds = page.Select(x => x.AlbumId).ToHashSet();
        var albums = await dbContext.Albums.AsNoTracking()
            .Where(a => albumIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);

        return page
            .Where(x => albums.ContainsKey(x.AlbumId))
            .Select(x => new AlbumStats(albums[x.AlbumId], x.TotalPlays, TimeSpan.FromTicks(x.TotalDurationTicks), x.GlobalRank));
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

        var statsQuery = query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .GroupBy(x => x.s.AlbumId)
            .Select(g => new { AlbumId = g.Key, TotalPlays = g.Sum(x => x.lh.IsEligibleForScrobbling || x.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0) })
            .Where(s => s.TotalPlays > 0 && s.AlbumId != null);

        if (string.IsNullOrWhiteSpace(searchTerm))
            return await statsQuery.CountAsync(ct).ConfigureAwait(false);

        var allStats = await statsQuery.ToListAsync(ct).ConfigureAwait(false);
        var allIds = allStats.Where(s => s.AlbumId.HasValue).Select(s => s.AlbumId!.Value).ToHashSet();
        var titleMap = await dbContext.Albums.AsNoTracking()
            .Where(a => allIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Title })
            .ToDictionaryAsync(a => a.Id, a => a.Title, ct).ConfigureAwait(false);

        return allStats.Count(s => s.AlbumId.HasValue && titleMap.TryGetValue(s.AlbumId.Value, out var t) && t.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
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

        // Aggregate WITHOUT search filter so global ranks are based on the full dataset.
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

        List<(int GlobalRank, Guid GenreId, int TotalPlays, long TotalDurationTicks)> page;

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            // No search: DB handles pagination. Global rank = offset + position + 1.
            var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
            page = topItems.Select((s, i) => (offset + i + 1, s.GenreId, s.TotalPlays, s.TotalDurationTicks)).ToList();
        }
        else
        {
            // Search active: fetch all ranked stats, filter by genre name in-memory, then paginate.
            var allStats = await statsQuery.ToListAsync(ct).ConfigureAwait(false);
            var allIds = allStats.Select(s => s.GenreId).ToHashSet();
            var nameMap = await dbContext.Genres.AsNoTracking()
                .Where(g => allIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Name })
                .ToDictionaryAsync(g => g.Id, g => g.Name, ct).ConfigureAwait(false);

            page = allStats
                .Select((s, i) => (GlobalRank: i + 1, s.GenreId, s.TotalPlays, s.TotalDurationTicks))
                .Where(x => nameMap.TryGetValue(x.GenreId, out var n) && n.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                .Skip(offset).Take(limit)
                .ToList();
        }

        var genreIds = page.Select(x => x.GenreId).ToHashSet();
        var genres = await dbContext.Genres.AsNoTracking()
            .Where(g => genreIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct).ConfigureAwait(false);

        return page
            .Where(x => genres.ContainsKey(x.GenreId))
            .Select(x => new GenreStats(genres[x.GenreId], x.TotalPlays, TimeSpan.FromTicks(x.TotalDurationTicks), x.GlobalRank));
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

        var statsQuery = query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .SelectMany(x => x.s.Genres, (x, g) => new { x.lh, g.Id })
            .GroupBy(x => x.Id)
            .Select(g => new { GenreId = g.Key, TotalPlays = g.Sum(x => x.lh.IsEligibleForScrobbling || x.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0) })
            .Where(s => s.TotalPlays > 0);

        if (string.IsNullOrWhiteSpace(searchTerm))
            return await statsQuery.CountAsync(ct).ConfigureAwait(false);

        var allStats = await statsQuery.ToListAsync(ct).ConfigureAwait(false);
        var allIds = allStats.Select(s => s.GenreId).ToHashSet();
        var nameMap = await dbContext.Genres.AsNoTracking()
            .Where(g => allIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Name })
            .ToDictionaryAsync(g => g.Id, g => g.Name, ct).ConfigureAwait(false);

        return allStats.Count(s => nameMap.TryGetValue(s.GenreId, out var n) && n.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
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
