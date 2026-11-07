using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

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

        // Guard against double-finalization races. Once a session is marked Finished,
        // it must not be downgraded to Skipped or PausedAndAbandoned by a late fire-and-forget.
        if (historyEntry.EndReason == PlaybackEndReason.Finished)
        {
            _logger.LogDebug("FinalizeListenSessionAsync: Session {Id} is already finalized as Finished. Skipping.", listenHistoryId);
            return;
        }

        var wasSkipped = historyEntry.EndReason == PlaybackEndReason.Skipped;
        historyEntry.Song.TotalListenTimeTicks += finalDuration.Ticks - historyEntry.ListenDurationTicks;
        historyEntry.ListenDurationTicks = finalDuration.Ticks;
        historyEntry.EndReason = endReason;

        if (!historyEntry.IsEligibleForScrobbling)
        {
            var isSkipped = endReason == PlaybackEndReason.Skipped;
            historyEntry.Song.SkipCount += (isSkipped ? 1 : 0) - (wasSkipped ? 1 : 0);
        }

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
            historyEntry.IsEligibleForScrobbling = true;
            historyEntry.Song.PlayCount++;
            historyEntry.Song.LastPlayedDate = DateTime.UtcNow;
            if (historyEntry.EndReason == PlaybackEndReason.Skipped)
                historyEntry.Song.SkipCount--;
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> MarkListenAsScrobbledAsync(long listenHistoryId)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await dbContext.ListenHistory.Where(history => history.Id == listenHistoryId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.IsScrobbled, true))
            .ConfigureAwait(false) > 0;
    }

    public async Task<bool> MarkListenAsSubmittedToListenBrainzAsync(long listenHistoryId)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await dbContext.ListenHistory.Where(history => history.Id == listenHistoryId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.IsSubmittedToListenBrainz, true))
            .ConfigureAwait(false) > 0;
    }

    public async Task ClearListenHistoryAsync()
    {
        _logger.LogInformation("Clearing all listen history.");
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await context.ListenHistory.ExecuteDeleteAsync().ConfigureAwait(false);
        _logger.LogInformation("Listen history cleared.");
        _notifier.Raise(LibraryChangeKind.ListenHistoryCleared);
    }
}
