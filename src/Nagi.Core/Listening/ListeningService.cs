using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Library;
using Nagi.Core.Playback;

namespace Nagi.Core.Listening;

public sealed class ListeningService : IListeningService
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly LibraryNotifier _notifier;
    private readonly ILogger<ListeningService> _logger;

    public ListeningService(IDbContextFactory<MusicDbContext> contextFactory, LibraryNotifier notifier,
        ILogger<ListeningService> logger)
    {
        _contextFactory = contextFactory;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<bool> SetSongsLovedAsync(IEnumerable<Guid> songIds, bool loved)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
        var updated = 0;
        foreach (var ids in songIds.Distinct().Chunk(MusicDbContext.MaxParametersPerQuery))
            updated += await context.Songs.Where(s => ids.Contains(s.Id) && s.IsLoved != loved)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.IsLoved, loved)
                    // The server is told in the background; until then a sync keeps the local value.
                    .SetProperty(s => s.IsLovedSyncPending, s => s.RemoteId != null)
                    // The song no longer matches what the server last said, so the next sync must read it again
                    // even if the server describes it as before, for instance after a favorite changed in another app.
                    .SetProperty(s => s.RemoteHash, (string?)null))
                .ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        if (updated > 0) _notifier.Raise(LibraryChangeKind.FavoritesChanged);
        return updated > 0;
    }

    public async Task<long?> StartListenSessionAsync(Guid songId, PlaybackContext context)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        if (!await dbContext.Songs.AnyAsync(s => s.Id == songId).ConfigureAwait(false)) return null;

        var historyEntry = new ListenHistory
        {
            SongId = songId,
            ListenTimestampUtc = DateTime.UtcNow,
            ContextType = context.Type,
            ContextId = context.ContextId,
            EndReason = PlaybackEndReason.PausedAndAbandoned // Default until finalized
        };

        dbContext.ListenHistory.Add(historyEntry);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        return historyEntry.Id;
    }

    public async Task FinalizeListenSessionAsync(long listenHistoryId, TimeSpan finalDuration, PlaybackEndReason endReason)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        // Serialize the read and counter updates with eligibility changes from playback callbacks.
        await using var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
        var historyEntry = await dbContext.ListenHistory
            .Include(lh => lh.Song)
            .FirstOrDefaultAsync(lh => lh.Id == listenHistoryId)
            .ConfigureAwait(false);

        if (historyEntry is null) return;

        var wasSkip = ListenRules.CountsAsSkip(historyEntry);
        historyEntry.Song.TotalListenTimeTicks += finalDuration.Ticks - historyEntry.ListenDurationTicks;
        historyEntry.ListenDurationTicks = finalDuration.Ticks;
        historyEntry.EndReason = endReason;
        historyEntry.Song.SkipCount += SkipCountChange(wasSkip, historyEntry);

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    public async Task<bool> MarkListenAsEligibleForScrobblingAsync(long listenHistoryId)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
        var historyEntry = await dbContext.ListenHistory
            .Include(lh => lh.Song)
            .FirstOrDefaultAsync(lh => lh.Id == listenHistoryId)
            .ConfigureAwait(false);

        if (historyEntry is null) return false;

        if (!historyEntry.IsEligibleForScrobbling)
        {
            var wasSkip = ListenRules.CountsAsSkip(historyEntry);
            historyEntry.IsEligibleForScrobbling = true;
            historyEntry.Song.PlayCount++;
            historyEntry.Song.LastPlayedDate = DateTime.UtcNow;
            historyEntry.Song.SkipCount += SkipCountChange(wasSkip, historyEntry);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
        return true;
    }

    private static int SkipCountChange(bool wasSkip, ListenHistory listen) =>
        (ListenRules.CountsAsSkip(listen) ? 1 : 0) - (wasSkip ? 1 : 0);

    public async Task ClearListenHistoryAsync()
    {
        _logger.LogInformation("Clearing all listen history.");
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await context.ListenHistory.ExecuteDeleteAsync().ConfigureAwait(false);
        _logger.LogInformation("Listen history cleared.");
        _notifier.Raise(LibraryChangeKind.ListenHistoryCleared);
    }

    public async Task<IReadOnlyList<RecentListen>> GetRecentListensAsync(string searchTerm, int skip, int take, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var query = context.ListenHistory.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var songs = SongQuery.Matching(context, context.Songs, searchTerm).Select(s => s.Id);
            query = query.Where(h => songs.Contains(h.SongId));
        }
        return await query.OrderByDescending(h => h.ListenTimestampUtc).ThenByDescending(h => h.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, RecentListen.MaxPageSize))
            .Select(h => new RecentListen(h.Id, h.SongId, h.Song.Title, h.Song.ArtistName, h.ListenTimestampUtc))
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<RediscoverySuggestion?> GetRediscoverySuggestionAsync(CancellationToken token = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-90);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Songs.AsNoTracking().Where(s => s.PlayCount >= 3 && s.LastPlayedDate < cutoff)
            .OrderByDescending(s => s.IsLoved).ThenByDescending(s => s.PlayCount).ThenBy(s => s.LastPlayedDate).ThenBy(s => s.Id)
            .Select(s => new RediscoverySuggestion(s.Id, s.Title, s.ArtistName, s.LastPlayedDate!.Value))
            .FirstOrDefaultAsync(token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MixCandidate>> GetMixCandidatesAsync(IEnumerable<Guid> songIds, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var result = new List<MixCandidate>();
        foreach (var ids in songIds.Distinct().Chunk(MusicDbContext.MaxParametersPerQuery))
            result.AddRange(await context.Songs.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new MixCandidate(s.Id, s.PrimaryArtistName, s.DurationTicks, s.PlayCount, s.LastPlayedDate))
                .ToListAsync(token).ConfigureAwait(false));
        return result;
    }
}
