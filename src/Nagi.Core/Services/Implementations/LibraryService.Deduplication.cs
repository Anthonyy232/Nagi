using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Collapses folder and song rows that point at the same file under different path spellings.
/// </summary>
public partial class LibraryService
{
    /// <summary>
    ///     Detects and collapses library rows that represent the same physical folder or file
    ///     under different path representations (e.g., mapped drive vs UNC, pre-canonicalization
    ///     differences), preserving playlist membership and listening history.
    ///     Returns the number of duplicate rows removed.
    /// </summary>
    public async Task<int> DeduplicateLibraryAsync(CancellationToken cancellationToken = default)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        cancellationToken = operationCts.Token;
        await _scanSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var foldersCollapsed = await CollapseDuplicateFoldersAsync(context, cancellationToken).ConfigureAwait(false);
            var songsCollapsed = await CollapseDuplicateSongsAsync(context, cancellationToken).ConfigureAwait(false);

            var removed = foldersCollapsed + songsCollapsed;
            var orphanedArtists = removed > 0
                ? await CleanUpOrphanedEntitiesAsync(context, cancellationToken).ConfigureAwait(false)
                : [];
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (removed > 0)
            {
                DeleteOrphanedArtistImages(orphanedArtists);
                LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.LibraryRescanned));
                _logger.LogInformation("Library deduplication merged {FolderDupes} folder duplicates and {SongDupes} song duplicates.",
                    foldersCollapsed, songsCollapsed);
            }

            return removed;
        }
        finally
        {
            _scanSemaphore.Release();
        }
    }

    private async Task<int> CollapseDuplicateFoldersAsync(MusicDbContext context, CancellationToken ct)
    {
        var folders = await context.Folders.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var groups = folders
            .GroupBy(f => _fileSystem.NormalizePath(f.Path), StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var group in groups)
        {
            var survivor = group
                .OrderBy(f => f.ParentFolderId.HasValue)
                .ThenBy(f => f.LastModifiedDate ?? DateTime.MaxValue)
                .ThenBy(f => f.Id)
                .First();

            foreach (var dup in group.Where(f => f.Id != survivor.Id))
            {
                await context.Folders
                    .Where(f => f.ParentFolderId == dup.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.ParentFolderId, survivor.Id), ct)
                    .ConfigureAwait(false);

                await context.Songs
                    .Where(s => s.FolderId == dup.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.FolderId, survivor.Id), ct)
                    .ConfigureAwait(false);

                await context.ListenHistory
                    .Where(history => history.ContextType == PlaybackContextType.Folder && history.ContextId == dup.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.ContextId, survivor.Id), ct)
                    .ConfigureAwait(false);

                removed += await context.Folders.Where(f => f.Id == dup.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            // Remove aliases before renaming: the canonical spelling may belong to a deleted row.
            if (survivor.Path != group.Key)
                await context.Folders.Where(f => f.Id == survivor.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.Path, group.Key), ct).ConfigureAwait(false);
        }

        return removed;
    }

    private async Task<int> CollapseDuplicateSongsAsync(MusicDbContext context, CancellationToken ct)
    {
        var songs = await context.Songs
            .AsNoTracking()
            .Select(s => new
            {
                s.Id, s.FilePath, s.PlayCount, s.SkipCount, s.TotalListenTimeTicks,
                s.IsLoved, s.Rating, s.DateAddedToLibrary, s.LastPlayedDate
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var removed = 0;
        foreach (var group in songs.GroupBy(s => _fileSystem.NormalizePath(s.FilePath), StringComparer.OrdinalIgnoreCase))
        {
            var survivor = group
                .OrderByDescending(s => s.PlayCount)
                .ThenByDescending(s => s.IsLoved)
                .ThenByDescending(s => s.DateAddedToLibrary ?? DateTime.MinValue)
                .First();
            var duplicateIds = group.Where(s => s.Id != survivor.Id).Select(s => s.Id).ToArray();
            if (duplicateIds.Length > 0)
            {
                var ids = group.Select(s => s.Id).ToArray();
                var playlistEntries = await context.PlaylistSongs.AsNoTracking()
                    .Where(entry => ids.Contains(entry.SongId)).ToListAsync(ct).ConfigureAwait(false);
                await context.PlaylistSongs.Where(entry => ids.Contains(entry.SongId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                context.PlaylistSongs.AddRange(playlistEntries.GroupBy(entry => entry.PlaylistId)
                    .Select(playlist => new PlaylistSong
                    {
                        PlaylistId = playlist.Key, SongId = survivor.Id, Order = playlist.Min(entry => entry.Order)
                    }));
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                context.ChangeTracker.Clear();

                await context.ListenHistory.Where(history => duplicateIds.Contains(history.SongId))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.SongId, survivor.Id), ct)
                    .ConfigureAwait(false);
                removed += await context.Songs.Where(s => duplicateIds.Contains(s.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            if (duplicateIds.Length > 0 || survivor.FilePath != group.Key)
            {
                var directory = Path.GetDirectoryName(group.Key) ?? string.Empty;
                var playCount = group.Sum(s => s.PlayCount);
                var skipCount = group.Sum(s => s.SkipCount);
                var listenTicks = group.Sum(s => s.TotalListenTimeTicks);
                var loved = group.Any(s => s.IsLoved);
                var rating = group.Max(s => s.Rating);
                var added = group.Min(s => s.DateAddedToLibrary);
                var lastPlayed = group.Max(s => s.LastPlayedDate);
                await context.Songs.Where(s => s.Id == survivor.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.FilePath, group.Key)
                    .SetProperty(s => s.DirectoryPath, directory)
                    .SetProperty(s => s.PlayCount, playCount)
                    .SetProperty(s => s.SkipCount, skipCount)
                    .SetProperty(s => s.TotalListenTimeTicks, listenTicks)
                    .SetProperty(s => s.IsLoved, loved)
                    .SetProperty(s => s.Rating, rating)
                    .SetProperty(s => s.DateAddedToLibrary, added)
                    .SetProperty(s => s.LastPlayedDate, lastPlayed), ct).ConfigureAwait(false);
            }
        }

        return removed;
    }
}
