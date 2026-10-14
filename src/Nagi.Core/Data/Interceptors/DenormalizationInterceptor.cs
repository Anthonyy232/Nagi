using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nagi.Core.Helpers;
using Nagi.Core.Models;

namespace Nagi.Core.Data.Interceptors;

/// <summary>
///     Keeps artist display names and article-stripped sort keys in sync before saving.
/// </summary>
public class DenormalizationInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        SyncEntities(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        SyncEntities(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void SyncEntities(DbContext? context)
    {
        if (context == null) return;

        var songsToSync = new HashSet<Song>();
        var albumsToSync = new HashSet<Album>();

        var trackedSongsById = context.ChangeTracker.Entries<Song>().ToDictionary(e => e.Entity.Id);
        foreach (var entry in trackedSongsById.Values)
        {
            if (entry.State == EntityState.Added)
                songsToSync.Add(entry.Entity);
            else if (entry.State == EntityState.Modified && entry.Property(s => s.Title).IsModified)
                entry.Property(s => s.SortTitle).CurrentValue = SortKeyHelper.Normalize(entry.Entity.Title);
        }

        var songArtistEntries = context.ChangeTracker.Entries<SongArtist>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        foreach (var entry in songArtistEntries)
        {
            if (trackedSongsById.TryGetValue(entry.Entity.SongId, out var songEntry))
                songsToSync.Add(songEntry.Entity);
        }

        var trackedAlbumsById = context.ChangeTracker.Entries<Album>().ToDictionary(e => e.Entity.Id);
        foreach (var entry in trackedAlbumsById.Values)
        {
            if (entry.State == EntityState.Added)
                albumsToSync.Add(entry.Entity);
            else if (entry.State == EntityState.Modified && entry.Property(a => a.Title).IsModified)
                entry.Property(a => a.SortTitle).CurrentValue = SortKeyHelper.Normalize(entry.Entity.Title);
        }

        var albumArtistEntries = context.ChangeTracker.Entries<AlbumArtist>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        foreach (var entry in albumArtistEntries)
        {
            if (trackedAlbumsById.TryGetValue(entry.Entity.AlbumId, out var albumEntry))
                albumsToSync.Add(albumEntry.Entity);
        }

        const int chunkSize = 500;

        // Added entities already have their relationships in memory.
        var nonAddedSongIds = songsToSync
            .Where(s => trackedSongsById[s.Id].State != EntityState.Added)
            .Select(s => s.Id);

        foreach (var chunk in nonAddedSongIds.Chunk(chunkSize))
        {
            context.Set<SongArtist>()
                .Include(sa => sa.Artist)
                .Where(sa => chunk.Contains(sa.SongId))
                .Load();
        }

        foreach (var song in songsToSync)
        {
            song.SyncDenormalizedFields();
            // Bulk scans disable automatic change detection.
            trackedSongsById[song.Id].DetectChanges();
        }

        var nonAddedAlbumIds = albumsToSync
            .Where(a => trackedAlbumsById[a.Id].State != EntityState.Added)
            .Select(a => a.Id);

        foreach (var chunk in nonAddedAlbumIds.Chunk(chunkSize))
        {
            context.Set<AlbumArtist>()
                .Include(aa => aa.Artist)
                .Where(aa => chunk.Contains(aa.AlbumId))
                .Load();
        }

        foreach (var album in albumsToSync)
        {
            album.SyncDenormalizedFields();
            trackedAlbumsById[album.Id].DetectChanges();
        }

        foreach (var entry in context.ChangeTracker.Entries<Artist>()
                     .Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.State == EntityState.Modified && !entry.Property(a => a.Name).IsModified)
                continue;

            entry.Property(a => a.SortName).CurrentValue = SortKeyHelper.Normalize(entry.Entity.Name);
        }
    }
}
