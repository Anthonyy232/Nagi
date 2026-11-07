using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Library;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

public class StatisticsService : IStatisticsService
{
    // Song.PlayCount counts only scrobble-eligible listens (ListeningService); Insights also counts finished ones.
    private static readonly Expression<Func<ListenHistory, ScoredListen>> Score = listen => new ScoredListen
    {
        SongId = listen.SongId,
        CountsAsPlay = listen.IsEligibleForScrobbling || listen.EndReason == PlaybackEndReason.Finished,
        DurationTicks = listen.ListenDurationTicks
    };

    private static readonly EntityKind<Song> Songs = new(
        (_, listens) => listens.Select(listen => new Credit { EntityId = listen.SongId, Listen = listen }),
        (context, pattern) => context.Songs
            .Where(song => EF.Functions.Like(song.Title.ToLower(), pattern, LikePatternHelper.EscapeCharacter))
            .Select(song => song.Id),
        (context, ids, ct) => context.Songs.AsNoTracking().Where(song => ids.Contains(song.Id)).ToDictionaryAsync(song => song.Id, ct));

    private static readonly EntityKind<Artist> Artists = new(
        (context, listens) => listens.Join(context.SongArtists, listen => listen.SongId, songArtist => songArtist.SongId,
            (listen, songArtist) => new Credit { EntityId = songArtist.ArtistId, Listen = listen }),
        (context, pattern) => context.Artists
            .Where(artist => EF.Functions.Like(artist.Name.ToLower(), pattern, LikePatternHelper.EscapeCharacter))
            .Select(artist => artist.Id),
        (context, ids, ct) => context.Artists.AsNoTracking().Where(artist => ids.Contains(artist.Id)).ToDictionaryAsync(artist => artist.Id, ct));

    private static readonly EntityKind<Album> Albums = new(
        (context, listens) => listens.Join(context.Songs.Where(song => song.AlbumId != null), listen => listen.SongId, song => song.Id,
            (listen, song) => new Credit { EntityId = song.AlbumId!.Value, Listen = listen }),
        (context, pattern) => context.Albums
            .Where(album => EF.Functions.Like(album.Title.ToLower(), pattern, LikePatternHelper.EscapeCharacter))
            .Select(album => album.Id),
        (context, ids, ct) => context.Albums.AsNoTracking().Where(album => ids.Contains(album.Id)).ToDictionaryAsync(album => album.Id, ct));

    private static readonly EntityKind<Genre> Genres = new(
        (context, listens) => listens.Join(context.Songs, listen => listen.SongId, song => song.Id, (listen, song) => new { listen, song })
            .SelectMany(row => row.song.Genres, (row, genre) => new Credit { EntityId = genre.Id, Listen = row.listen }),
        (context, pattern) => context.Genres
            .Where(genre => EF.Functions.Like(genre.Name.ToLower(), pattern, LikePatternHelper.EscapeCharacter))
            .Select(genre => genre.Id),
        (context, ids, ct) => context.Genres.AsNoTracking().Where(genre => ids.Contains(genre.Id)).ToDictionaryAsync(genre => genre.Id, ct));

    private readonly IDbContextFactory<MusicDbContext> _contextFactory;

    public StatisticsService(IDbContextFactory<MusicDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public Task<StatisticsPage<SongStats>> GetTopSongsAsync(TimeRange range, SortMetric metric, int offset, int limit, string? searchTerm = null, CancellationToken ct = default) =>
        GetTopAsync(Songs, range, metric, offset, limit, searchTerm,
            (song, row) => new SongStats(song, row.Plays, TimeSpan.FromTicks(row.DurationTicks), row.Rank), ct);

    public Task<StatisticsPage<ArtistStats>> GetTopArtistsAsync(TimeRange range, SortMetric metric, int offset, int limit, string? searchTerm = null, CancellationToken ct = default) =>
        GetTopAsync(Artists, range, metric, offset, limit, searchTerm,
            (artist, row) => new ArtistStats(artist, row.Plays, TimeSpan.FromTicks(row.DurationTicks), row.Rank), ct);

    public Task<StatisticsPage<AlbumStats>> GetTopAlbumsAsync(TimeRange range, SortMetric metric, int offset, int limit, string? searchTerm = null, CancellationToken ct = default) =>
        GetTopAsync(Albums, range, metric, offset, limit, searchTerm,
            (album, row) => new AlbumStats(album, row.Plays, TimeSpan.FromTicks(row.DurationTicks), row.Rank), ct);

    public Task<StatisticsPage<GenreStats>> GetTopGenresAsync(TimeRange range, SortMetric metric, int offset, int limit, string? searchTerm = null, CancellationToken ct = default) =>
        GetTopAsync(Genres, range, metric, offset, limit, searchTerm,
            (genre, row) => new GenreStats(genre, row.Plays, TimeSpan.FromTicks(row.DurationTicks), row.Rank), ct);

    private async Task<StatisticsPage<TStats>> GetTopAsync<TEntity, TStats>(
        EntityKind<TEntity> kind,
        TimeRange range,
        SortMetric metric,
        int offset,
        int limit,
        string? searchTerm,
        Func<TEntity, RankedRow, TStats> create,
        CancellationToken ct)
    {
        offset = Math.Max(0, offset);
        limit = Math.Max(0, limit);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var totals = kind.Credit(dbContext, ApplyTimeRange(dbContext.ListenHistory.AsNoTracking(), range).Select(Score))
            .GroupBy(credit => credit.EntityId)
            .Select(group => new RankedRow
            {
                Id = group.Key,
                Plays = group.Sum(credit => credit.Listen.CountsAsPlay ? 1 : 0),
                DurationTicks = group.Sum(credit => credit.Listen.DurationTicks)
            })
            .Where(row => row.Plays > 0);
        var ranked = metric == SortMetric.PlayCount
            ? totals.OrderByDescending(row => row.Plays).ThenBy(row => row.Id)
            : totals.OrderByDescending(row => row.DurationTicks).ThenBy(row => row.Id);

        // A search filters the full ranking rather than ranking only the matches, so each item keeps its place.
        var normalizedTerm = TextNormalizer.Normalize(searchTerm);
        var matching = normalizedTerm is null
            ? null
            : kind.Matching(dbContext, LikePatternHelper.CreateContainsPattern(normalizedTerm.ToLowerInvariant()));
        var rows = matching is null
            ? ranked.Select(row => new { Row = row, Matches = true })
            : ranked.Select(row => new { Row = row, Matches = matching.Contains(row.Id) });

        var pageRows = new List<RankedRow>();
        var rank = 0;
        var totalCount = 0;
        await foreach (var candidate in rows.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            rank++;
            if (!candidate.Matches) continue;

            if (totalCount >= offset && pageRows.Count < limit)
            {
                candidate.Row.Rank = rank;
                pageRows.Add(candidate.Row);
            }
            totalCount++;
        }

        if (pageRows.Count == 0) return new StatisticsPage<TStats>([], totalCount);

        var entities = await kind.Load(dbContext, pageRows.Select(row => row.Id).ToArray(), ct).ConfigureAwait(false);
        var items = pageRows
            .Where(row => entities.ContainsKey(row.Id))
            .Select(row => create(entities[row.Id], row))
            .ToList();
        return new StatisticsPage<TStats>(items, totalCount);
    }

    private static IQueryable<ListenHistory> ApplyTimeRange(IQueryable<ListenHistory> query, TimeRange range)
    {
        if (range.StartUtc.HasValue)
            query = query.Where(history => history.ListenTimestampUtc >= range.StartUtc.Value);
        if (range.EndUtc.HasValue)
            query = query.Where(history => history.ListenTimestampUtc <= range.EndUtc.Value);
        return query;
    }

    public async Task<TimeSpan> GetTotalListenTimeAsync(TimeRange range, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = ApplyTimeRange(dbContext.ListenHistory.AsNoTracking(), range);

        var totalTicks = await query.SumAsync(lh => lh.ListenDurationTicks, ct).ConfigureAwait(false);
        return TimeSpan.FromTicks(totalTicks);
    }

    public async Task<ListeningPatternStats> GetListeningPatternsAsync(TimeRange range, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = ApplyTimeRange(dbContext.ListenHistory.AsNoTracking(), range);

        // Convert locally so history uses the same time-zone rules as the rest of the app.
        var counts = await query
            .Select(lh => lh.ListenTimestampUtc)
            .AsAsyncEnumerable()
            .Select(timestamp => timestamp.ToLocalTime())
            .CountBy(timestamp => (timestamp.DayOfWeek, timestamp.Hour))
            .ToListAsync(ct).ConfigureAwait(false);

        if (counts.Count == 0) return new ListeningPatternStats(DayOfWeek.Monday, 12);

        var mostActiveDay = counts
            .AggregateBy(entry => entry.Key.DayOfWeek, 0, (total, entry) => total + entry.Value)
            .MaxBy(entry => entry.Value).Key;
        var peakHour = counts
            .AggregateBy(entry => entry.Key.Hour, 0, (total, entry) => total + entry.Value)
            .MaxBy(entry => entry.Value).Key;

        return new ListeningPatternStats(mostActiveDay, peakHour);
    }

    public async Task<IEnumerable<ContextStats>> GetPlaybackSourceDistributionAsync(TimeRange range, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = ApplyTimeRange(dbContext.ListenHistory.AsNoTracking(), range);

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

    private sealed class ScoredListen
    {
        public Guid SongId { get; init; }
        public bool CountsAsPlay { get; init; }
        public long DurationTicks { get; init; }
    }

    private sealed class Credit
    {
        public Guid EntityId { get; init; }
        public ScoredListen Listen { get; init; } = null!;
    }

    private sealed class RankedRow
    {
        public Guid Id { get; init; }
        public int Plays { get; init; }
        public long DurationTicks { get; init; }
        public int Rank { get; set; }
    }

    private sealed record EntityKind<TEntity>(
        Func<MusicDbContext, IQueryable<ScoredListen>, IQueryable<Credit>> Credit,
        Func<MusicDbContext, string, IQueryable<Guid>> Matching,
        Func<MusicDbContext, Guid[], CancellationToken, Task<Dictionary<Guid, TEntity>>> Load);
}
