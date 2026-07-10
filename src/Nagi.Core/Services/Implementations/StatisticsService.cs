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
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return (await GetTopSongsSearchPageAsync(range, limit, metric, offset, searchTerm, ct, includeTotalCount: false).ConfigureAwait(false)).Items;

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

        // No search: DB handles pagination. Global rank = offset + position + 1.
        var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        var page = topItems
            .Select((s, i) => (GlobalRank: offset + i + 1, s.SongId, s.TotalPlays, s.TotalDurationTicks, s.Skips))
            .ToList();

        var songIds = page.Select(x => x.SongId).ToHashSet();
        var songs = await dbContext.Songs.AsNoTracking()
            .Where(s => songIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);

        return page
            .Where(x => songs.ContainsKey(x.SongId))
            .Select(x => new SongStats(songs[x.SongId], x.TotalPlays, TimeSpan.FromTicks(x.TotalDurationTicks), x.Skips, x.GlobalRank));
    }

    /// <inheritdoc />
    public async Task<StatisticsPage<SongStats>> GetTopSongsPageAsync(TimeRange range, int limit, SortMetric metric, int offset, string? searchTerm, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return await GetTopSongsSearchPageAsync(range, limit, metric, offset, searchTerm, ct).ConfigureAwait(false);

        var totalCount = await GetTopSongsCountAsync(range, null, ct).ConfigureAwait(false);
        var items = (await GetTopSongsAsync(range, limit, metric, offset, null, ct).ConfigureAwait(false)).ToList();
        return new StatisticsPage<SongStats>(items, totalCount);
    }

    /// <inheritdoc />
    public async Task<int> GetTopSongsCountAsync(TimeRange range, string? searchTerm = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return (await GetTopSongsSearchPageAsync(range, 0, SortMetric.PlayCount, 0, searchTerm, ct).ConfigureAwait(false)).TotalCount;

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

        return await statsQuery.CountAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ArtistStats>> GetTopArtistsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.Duration, int offset = 0, string? searchTerm = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return (await GetTopArtistsSearchPageAsync(range, limit, metric, offset, searchTerm, ct, includeTotalCount: false).ConfigureAwait(false)).Items;

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

        // No search: DB handles pagination. Global rank = offset + position + 1.
        var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        var page = topItems
            .Select((s, i) => (GlobalRank: offset + i + 1, s.ArtistId, s.TotalPlays, s.TotalDurationTicks))
            .ToList();

        var artistIds = page.Select(x => x.ArtistId).ToHashSet();
        var artists = await dbContext.Artists.AsNoTracking()
            .Where(a => artistIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);

        return page
            .Where(x => artists.ContainsKey(x.ArtistId))
            .Select(x => new ArtistStats(artists[x.ArtistId], x.TotalPlays, TimeSpan.FromTicks(x.TotalDurationTicks), x.GlobalRank));
    }

    /// <inheritdoc />
    public async Task<StatisticsPage<ArtistStats>> GetTopArtistsPageAsync(TimeRange range, int limit, SortMetric metric, int offset, string? searchTerm, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return await GetTopArtistsSearchPageAsync(range, limit, metric, offset, searchTerm, ct).ConfigureAwait(false);

        var totalCount = await GetTopArtistsCountAsync(range, null, ct).ConfigureAwait(false);
        var items = (await GetTopArtistsAsync(range, limit, metric, offset, null, ct).ConfigureAwait(false)).ToList();
        return new StatisticsPage<ArtistStats>(items, totalCount);
    }

    /// <inheritdoc />
    public async Task<int> GetTopArtistsCountAsync(TimeRange range, string? searchTerm = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return (await GetTopArtistsSearchPageAsync(range, 0, SortMetric.PlayCount, 0, searchTerm, ct).ConfigureAwait(false)).TotalCount;

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

        return await statsQuery.CountAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AlbumStats>> GetTopAlbumsAsync(TimeRange range, int limit = 50, SortMetric metric = SortMetric.PlayCount, int offset = 0, string? searchTerm = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return (await GetTopAlbumsSearchPageAsync(range, limit, metric, offset, searchTerm, ct, includeTotalCount: false).ConfigureAwait(false)).Items;

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = dbContext.ListenHistory.AsNoTracking().AsQueryable();

        if (range.StartUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(lh => lh.ListenTimestampUtc <= range.EndUtc.Value);

        // Aggregate WITHOUT search filter so global ranks are based on the full dataset.
        var statsQuery = query
            .Join(dbContext.Songs, lh => lh.SongId, s => s.Id, (lh, s) => new { lh, s })
            .Where(x => x.s.AlbumId != null)
            .GroupBy(x => x.s.AlbumId)
            .Select(g => new
            {
                AlbumId = g.Key,
                TotalPlays = g.Sum(x => x.lh.IsEligibleForScrobbling || x.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0),
                TotalDurationTicks = g.Sum(x => x.lh.ListenDurationTicks)
            })
            .Where(s => s.TotalPlays > 0);

        if (metric == SortMetric.PlayCount)
            statsQuery = statsQuery.OrderByDescending(s => s.TotalPlays).ThenBy(s => s.AlbumId);
        else
            statsQuery = statsQuery.OrderByDescending(s => s.TotalDurationTicks).ThenBy(s => s.AlbumId);

        // No search: DB handles pagination. Global rank = offset + position + 1.
        var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        var page = topItems
            .Select((s, i) => (GlobalRank: offset + i + 1, s.AlbumId, s.TotalPlays, s.TotalDurationTicks))
            .Where(x => x.AlbumId.HasValue)
            .Select(x => (x.GlobalRank, AlbumId: x.AlbumId!.Value, x.TotalPlays, x.TotalDurationTicks))
            .ToList();

        var albumIds = page.Select(x => x.AlbumId).ToHashSet();
        var albums = await dbContext.Albums.AsNoTracking()
            .Where(a => albumIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);

        return page
            .Where(x => albums.ContainsKey(x.AlbumId))
            .Select(x => new AlbumStats(albums[x.AlbumId], x.TotalPlays, TimeSpan.FromTicks(x.TotalDurationTicks), x.GlobalRank));
    }

    /// <inheritdoc />
    public async Task<StatisticsPage<AlbumStats>> GetTopAlbumsPageAsync(TimeRange range, int limit, SortMetric metric, int offset, string? searchTerm, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return await GetTopAlbumsSearchPageAsync(range, limit, metric, offset, searchTerm, ct).ConfigureAwait(false);

        var totalCount = await GetTopAlbumsCountAsync(range, null, ct).ConfigureAwait(false);
        var items = (await GetTopAlbumsAsync(range, limit, metric, offset, null, ct).ConfigureAwait(false)).ToList();
        return new StatisticsPage<AlbumStats>(items, totalCount);
    }

    /// <inheritdoc />
    public async Task<int> GetTopAlbumsCountAsync(TimeRange range, string? searchTerm = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return (await GetTopAlbumsSearchPageAsync(range, 0, SortMetric.PlayCount, 0, searchTerm, ct).ConfigureAwait(false)).TotalCount;

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

        return await statsQuery.CountAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<GenreStats>> GetTopGenresAsync(TimeRange range, int limit = 10, SortMetric metric = SortMetric.PlayCount, int offset = 0, string? searchTerm = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return (await GetTopGenresSearchPageAsync(range, limit, metric, offset, searchTerm, ct, includeTotalCount: false).ConfigureAwait(false)).Items;

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
            .Where(s => s.TotalPlays > 0);

        if (metric == SortMetric.PlayCount)
            statsQuery = statsQuery.OrderByDescending(s => s.TotalPlays).ThenBy(s => s.GenreId);
        else
            statsQuery = statsQuery.OrderByDescending(s => s.TotalDurationTicks).ThenBy(s => s.GenreId);

        // No search: DB handles pagination. Global rank = offset + position + 1.
        var topItems = await statsQuery.Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        var page = topItems
            .Select((s, i) => (GlobalRank: offset + i + 1, s.GenreId, s.TotalPlays, s.TotalDurationTicks))
            .ToList();

        var genreIds = page.Select(x => x.GenreId).ToHashSet();
        var genres = await dbContext.Genres.AsNoTracking()
            .Where(g => genreIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct).ConfigureAwait(false);

        return page
            .Where(x => genres.ContainsKey(x.GenreId))
            .Select(x => new GenreStats(genres[x.GenreId], x.TotalPlays, TimeSpan.FromTicks(x.TotalDurationTicks), x.GlobalRank));
    }

    /// <inheritdoc />
    public async Task<StatisticsPage<GenreStats>> GetTopGenresPageAsync(TimeRange range, int limit, SortMetric metric, int offset, string? searchTerm, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return await GetTopGenresSearchPageAsync(range, limit, metric, offset, searchTerm, ct).ConfigureAwait(false);

        var totalCount = await GetTopGenresCountAsync(range, null, ct).ConfigureAwait(false);
        var items = (await GetTopGenresAsync(range, limit, metric, offset, null, ct).ConfigureAwait(false)).ToList();
        return new StatisticsPage<GenreStats>(items, totalCount);
    }

    /// <inheritdoc />
    public async Task<int> GetTopGenresCountAsync(TimeRange range, string? searchTerm = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
            return (await GetTopGenresSearchPageAsync(range, 0, SortMetric.PlayCount, 0, searchTerm, ct).ConfigureAwait(false)).TotalCount;

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

        return await statsQuery.CountAsync(ct).ConfigureAwait(false);
    }

    private async Task<StatisticsPage<SongStats>> GetTopSongsSearchPageAsync(
        TimeRange range,
        int limit,
        SortMetric metric,
        int offset,
        string searchTerm,
        CancellationToken ct,
        bool includeTotalCount = true)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var history = ApplyTimeRange(dbContext.ListenHistory.AsNoTracking(), range);
        var aggregates = history.GroupBy(lh => lh.SongId)
            .Select(g => new
            {
                SongId = g.Key,
                TotalPlays = g.Sum(lh => lh.IsEligibleForScrobbling || lh.EndReason == PlaybackEndReason.Finished ? 1 : 0),
                TotalDurationTicks = g.Sum(lh => lh.ListenDurationTicks),
                Skips = g.Sum(lh => lh.EndReason == PlaybackEndReason.Skipped && !lh.IsEligibleForScrobbling ? 1 : 0)
            })
            .Where(row => row.TotalPlays > 0);
        var rows = aggregates.Join(
            dbContext.Songs.AsNoTracking(),
            aggregate => aggregate.SongId,
            song => song.Id,
            (aggregate, song) => new
            {
                aggregate.SongId,
                aggregate.TotalPlays,
                aggregate.TotalDurationTicks,
                aggregate.Skips,
                song.Title
            });
        var rankedRows = metric == SortMetric.PlayCount
            ? rows.OrderByDescending(row => row.TotalPlays).ThenBy(row => row.SongId)
            : rows.OrderByDescending(row => row.TotalDurationTicks).ThenBy(row => row.SongId);

        var pageRows = new List<(int Rank, Guid SongId, int Plays, long DurationTicks, int Skips)>();
        var globalRank = 0;
        var totalCount = 0;
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Max(0, limit);
        await foreach (var row in rankedRows.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            globalRank++;
            if (!row.Title.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) continue;

            if (totalCount >= safeOffset && pageRows.Count < safeLimit)
                pageRows.Add((globalRank, row.SongId, row.TotalPlays, row.TotalDurationTicks, row.Skips));
            totalCount++;
            if (!includeTotalCount && pageRows.Count >= safeLimit) break;
        }

        if (pageRows.Count == 0) return new StatisticsPage<SongStats>([], totalCount);

        var ids = pageRows.Select(row => row.SongId).ToHashSet();
        var entities = await dbContext.Songs.AsNoTracking()
            .Where(song => ids.Contains(song.Id))
            .ToDictionaryAsync(song => song.Id, ct)
            .ConfigureAwait(false);
        var items = pageRows
            .Where(row => entities.ContainsKey(row.SongId))
            .Select(row => new SongStats(entities[row.SongId], row.Plays, TimeSpan.FromTicks(row.DurationTicks), row.Skips, row.Rank))
            .ToList();
        return new StatisticsPage<SongStats>(items, totalCount);
    }

    private async Task<StatisticsPage<ArtistStats>> GetTopArtistsSearchPageAsync(
        TimeRange range,
        int limit,
        SortMetric metric,
        int offset,
        string searchTerm,
        CancellationToken ct,
        bool includeTotalCount = true)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var history = ApplyTimeRange(dbContext.ListenHistory.AsNoTracking(), range);
        var aggregates = history
            .Join(dbContext.Songs, lh => lh.SongId, song => song.Id, (lh, song) => new { lh, song })
            .SelectMany(row => row.song.SongArtists, (row, songArtist) => new { row.lh, songArtist.ArtistId })
            .GroupBy(row => row.ArtistId)
            .Select(g => new
            {
                ArtistId = g.Key,
                TotalPlays = g.Sum(row => row.lh.IsEligibleForScrobbling || row.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0),
                TotalDurationTicks = g.Sum(row => row.lh.ListenDurationTicks)
            })
            .Where(row => row.TotalPlays > 0);
        var rows = aggregates.Join(
            dbContext.Artists.AsNoTracking(),
            aggregate => aggregate.ArtistId,
            artist => artist.Id,
            (aggregate, artist) => new
            {
                aggregate.ArtistId,
                aggregate.TotalPlays,
                aggregate.TotalDurationTicks,
                artist.Name
            });
        var rankedRows = metric == SortMetric.PlayCount
            ? rows.OrderByDescending(row => row.TotalPlays).ThenBy(row => row.ArtistId)
            : rows.OrderByDescending(row => row.TotalDurationTicks).ThenBy(row => row.ArtistId);

        var pageRows = new List<(int Rank, Guid ArtistId, int Plays, long DurationTicks)>();
        var globalRank = 0;
        var totalCount = 0;
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Max(0, limit);
        await foreach (var row in rankedRows.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            globalRank++;
            if (!row.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) continue;

            if (totalCount >= safeOffset && pageRows.Count < safeLimit)
                pageRows.Add((globalRank, row.ArtistId, row.TotalPlays, row.TotalDurationTicks));
            totalCount++;
            if (!includeTotalCount && pageRows.Count >= safeLimit) break;
        }

        if (pageRows.Count == 0) return new StatisticsPage<ArtistStats>([], totalCount);

        var ids = pageRows.Select(row => row.ArtistId).ToHashSet();
        var entities = await dbContext.Artists.AsNoTracking()
            .Where(artist => ids.Contains(artist.Id))
            .ToDictionaryAsync(artist => artist.Id, ct)
            .ConfigureAwait(false);
        var items = pageRows
            .Where(row => entities.ContainsKey(row.ArtistId))
            .Select(row => new ArtistStats(entities[row.ArtistId], row.Plays, TimeSpan.FromTicks(row.DurationTicks), row.Rank))
            .ToList();
        return new StatisticsPage<ArtistStats>(items, totalCount);
    }

    private async Task<StatisticsPage<AlbumStats>> GetTopAlbumsSearchPageAsync(
        TimeRange range,
        int limit,
        SortMetric metric,
        int offset,
        string searchTerm,
        CancellationToken ct,
        bool includeTotalCount = true)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var history = ApplyTimeRange(dbContext.ListenHistory.AsNoTracking(), range);
        var aggregates = history
            .Join(dbContext.Songs, lh => lh.SongId, song => song.Id, (lh, song) => new { lh, song })
            .Where(row => row.song.AlbumId != null)
            .GroupBy(row => row.song.AlbumId)
            .Select(g => new
            {
                AlbumId = g.Key,
                TotalPlays = g.Sum(row => row.lh.IsEligibleForScrobbling || row.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0),
                TotalDurationTicks = g.Sum(row => row.lh.ListenDurationTicks)
            })
            .Where(row => row.TotalPlays > 0);
        var rows = aggregates.Join(
            dbContext.Albums.AsNoTracking(),
            aggregate => aggregate.AlbumId,
            album => (Guid?)album.Id,
            (aggregate, album) => new
            {
                aggregate.AlbumId,
                aggregate.TotalPlays,
                aggregate.TotalDurationTicks,
                album.Title
            });
        var rankedRows = metric == SortMetric.PlayCount
            ? rows.OrderByDescending(row => row.TotalPlays).ThenBy(row => row.AlbumId)
            : rows.OrderByDescending(row => row.TotalDurationTicks).ThenBy(row => row.AlbumId);

        var pageRows = new List<(int Rank, Guid AlbumId, int Plays, long DurationTicks)>();
        var globalRank = 0;
        var totalCount = 0;
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Max(0, limit);
        await foreach (var row in rankedRows.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            globalRank++;
            if (!row.Title.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) continue;

            if (totalCount >= safeOffset && pageRows.Count < safeLimit && row.AlbumId.HasValue)
                pageRows.Add((globalRank, row.AlbumId.Value, row.TotalPlays, row.TotalDurationTicks));
            totalCount++;
            if (!includeTotalCount && pageRows.Count >= safeLimit) break;
        }

        if (pageRows.Count == 0) return new StatisticsPage<AlbumStats>([], totalCount);

        var ids = pageRows.Select(row => row.AlbumId).ToHashSet();
        var entities = await dbContext.Albums.AsNoTracking()
            .Where(album => ids.Contains(album.Id))
            .ToDictionaryAsync(album => album.Id, ct)
            .ConfigureAwait(false);
        var items = pageRows
            .Where(row => entities.ContainsKey(row.AlbumId))
            .Select(row => new AlbumStats(entities[row.AlbumId], row.Plays, TimeSpan.FromTicks(row.DurationTicks), row.Rank))
            .ToList();
        return new StatisticsPage<AlbumStats>(items, totalCount);
    }

    private async Task<StatisticsPage<GenreStats>> GetTopGenresSearchPageAsync(
        TimeRange range,
        int limit,
        SortMetric metric,
        int offset,
        string searchTerm,
        CancellationToken ct,
        bool includeTotalCount = true)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var history = ApplyTimeRange(dbContext.ListenHistory.AsNoTracking(), range);
        var aggregates = history
            .Join(dbContext.Songs, lh => lh.SongId, song => song.Id, (lh, song) => new { lh, song })
            .SelectMany(row => row.song.Genres, (row, genre) => new { row.lh, GenreId = genre.Id })
            .GroupBy(row => row.GenreId)
            .Select(g => new
            {
                GenreId = g.Key,
                TotalPlays = g.Sum(row => row.lh.IsEligibleForScrobbling || row.lh.EndReason == PlaybackEndReason.Finished ? 1 : 0),
                TotalDurationTicks = g.Sum(row => row.lh.ListenDurationTicks)
            })
            .Where(row => row.TotalPlays > 0);
        var rows = aggregates.Join(
            dbContext.Genres.AsNoTracking(),
            aggregate => aggregate.GenreId,
            genre => genre.Id,
            (aggregate, genre) => new
            {
                aggregate.GenreId,
                aggregate.TotalPlays,
                aggregate.TotalDurationTicks,
                genre.Name
            });
        var rankedRows = metric == SortMetric.PlayCount
            ? rows.OrderByDescending(row => row.TotalPlays).ThenBy(row => row.GenreId)
            : rows.OrderByDescending(row => row.TotalDurationTicks).ThenBy(row => row.GenreId);

        var pageRows = new List<(int Rank, Guid GenreId, int Plays, long DurationTicks)>();
        var globalRank = 0;
        var totalCount = 0;
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Max(0, limit);
        await foreach (var row in rankedRows.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            globalRank++;
            if (!row.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) continue;

            if (totalCount >= safeOffset && pageRows.Count < safeLimit)
                pageRows.Add((globalRank, row.GenreId, row.TotalPlays, row.TotalDurationTicks));
            totalCount++;
            if (!includeTotalCount && pageRows.Count >= safeLimit) break;
        }

        if (pageRows.Count == 0) return new StatisticsPage<GenreStats>([], totalCount);

        var ids = pageRows.Select(row => row.GenreId).ToHashSet();
        var entities = await dbContext.Genres.AsNoTracking()
            .Where(genre => ids.Contains(genre.Id))
            .ToDictionaryAsync(genre => genre.Id, ct)
            .ConfigureAwait(false);
        var items = pageRows
            .Where(row => entities.ContainsKey(row.GenreId))
            .Select(row => new GenreStats(entities[row.GenreId], row.Plays, TimeSpan.FromTicks(row.DurationTicks), row.Rank))
            .ToList();
        return new StatisticsPage<GenreStats>(items, totalCount);
    }

    private static IQueryable<ListenHistory> ApplyTimeRange(IQueryable<ListenHistory> query, TimeRange range)
    {
        if (range.StartUtc.HasValue)
            query = query.Where(history => history.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(history => history.ListenTimestampUtc <= range.EndUtc.Value);
        return query;
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
    public async Task<IEnumerable<ActivityDataPoint>> GetListeningActivityTimelineAsync(TimeRange range, ActivityInterval interval, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(interval))
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unknown activity interval.");

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = ApplyTimeRange(dbContext.ListenHistory.AsNoTracking(), range)
            .Select(history => new
            {
                history.ListenTimestampUtc,
                history.ListenDurationTicks,
                history.IsEligibleForScrobbling,
                history.EndReason
            });
        var buckets = new Dictionary<DateTime, (int Plays, long DurationTicks)>();

        await foreach (var history in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            var bucket = GetActivityBucket(history.ListenTimestampUtc.ToLocalTime(), interval);
            var current = buckets.GetValueOrDefault(bucket);
            var qualifyingPlay = history.IsEligibleForScrobbling || history.EndReason == PlaybackEndReason.Finished
                ? 1
                : 0;
            buckets[bucket] = (
                current.Plays + qualifyingPlay,
                current.DurationTicks + history.ListenDurationTicks);
        }

        return buckets
            .OrderBy(entry => entry.Key)
            .Select(entry => new ActivityDataPoint(
                entry.Key,
                entry.Value.Plays,
                TimeSpan.FromTicks(entry.Value.DurationTicks)))
            .ToList();
    }

    private static DateTime GetActivityBucket(DateTime localTimestamp, ActivityInterval interval)
    {
        return interval switch
        {
            ActivityInterval.Hour => new DateTime(
                localTimestamp.Year,
                localTimestamp.Month,
                localTimestamp.Day,
                localTimestamp.Hour,
                0,
                0,
                localTimestamp.Kind),
            ActivityInterval.Day => localTimestamp.Date,
            ActivityInterval.Week => localTimestamp.Date.AddDays(-(((int)localTimestamp.DayOfWeek + 6) % 7)),
            ActivityInterval.Month => new DateTime(
                localTimestamp.Year,
                localTimestamp.Month,
                1,
                0,
                0,
                0,
                localTimestamp.Kind),
            _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unknown activity interval.")
        };
    }

    /// <inheritdoc />
    public async Task<ListeningPatternStats> GetListeningPatternsAsync(TimeRange range, CancellationToken ct = default)
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

        if (timestamps.Count == 0) return new ListeningPatternStats(DayOfWeek.Monday, 12);

        var dayCounts = new Dictionary<DayOfWeek, int>();
        var hourCounts = new Dictionary<int, int>();
        foreach (var timestamp in timestamps)
        {
            var localTimestamp = timestamp.ToLocalTime();
            dayCounts[localTimestamp.DayOfWeek] = dayCounts.GetValueOrDefault(localTimestamp.DayOfWeek) + 1;
            hourCounts[localTimestamp.Hour] = hourCounts.GetValueOrDefault(localTimestamp.Hour) + 1;
        }

        var mostActiveDay = dayCounts.MaxBy(entry => entry.Value).Key;
        var peakHour = hourCounts.MaxBy(entry => entry.Value).Key;

        return new ListeningPatternStats(mostActiveDay, peakHour);
    }

    /// <inheritdoc />
    public async Task<DayOfWeek> GetMostActiveDayOfWeekAsync(TimeRange range, CancellationToken ct = default)
    {
        return (await GetListeningPatternsAsync(range, ct).ConfigureAwait(false)).MostActiveDay;
    }

    /// <inheritdoc />
    public async Task<int> GetPeakListeningHourAsync(TimeRange range, CancellationToken ct = default)
    {
        return (await GetListeningPatternsAsync(range, ct).ConfigureAwait(false)).PeakHour;
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
