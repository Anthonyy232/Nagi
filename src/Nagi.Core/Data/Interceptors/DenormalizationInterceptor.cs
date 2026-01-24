using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nagi.Core.Models;

namespace Nagi.Core.Data.Interceptors;

/// <summary>
///     An EF Core interceptor that automatically synchronizes denormalized artist fields 
///     (ArtistName and PrimaryArtistName) on Song and Album entities before they are saved to the database.
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

        // 1. Process Song changes
        // Collect Songs directly marked as Added or Modified
        var directSongEntries = context.ChangeTracker.Entries<Song>()
            .Where(e => e.State == EntityState.Added);
        foreach (var entry in directSongEntries) songsToSync.Add(entry.Entity);

        // Collect Songs where SongArtist relationships changed (Added, Modified, or Deleted)
        var songArtistEntries = context.ChangeTracker.Entries<SongArtist>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted);
        foreach (var sae in songArtistEntries)
        {
            // Find the parent song in the change tracker
            var song = context.ChangeTracker.Entries<Song>()
                .FirstOrDefault(e => e.Entity.Id == sae.Entity.SongId)?.Entity;
            if (song != null) songsToSync.Add(song);
        }

        // 2. Process Album changes
        // Collect Albums directly marked as Added or Modified
        var directAlbumEntries = context.ChangeTracker.Entries<Album>()
            .Where(e => e.State == EntityState.Added);
        foreach (var entry in directAlbumEntries) albumsToSync.Add(entry.Entity);

        // Collect Albums where AlbumArtist relationships changed
        var albumArtistEntries = context.ChangeTracker.Entries<AlbumArtist>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted);
        foreach (var aae in albumArtistEntries)
        {
            var album = context.ChangeTracker.Entries<Album>()
                .FirstOrDefault(e => e.Entity.Id == aae.Entity.AlbumId)?.Entity;
            if (album != null) albumsToSync.Add(album);
        }

        // 3. Batch-load relationships for all affected entities (fixes N+1 query problem)
        // Instead of loading per-entity (one query per song/album), we batch-load ALL
        // relationships in a single query per entity type.
        // We chunk the IDs to prevent SQL parameter limit issues (SQLite: 999, SQL Server: ~2100)
        const int chunkSize = 500;
        
        // Batch-load SongArtists for all non-Added songs
        var nonAddedSongIds = songsToSync
            .Where(s => context.Entry(s).State != EntityState.Added)
            .Select(s => s.Id)
            .ToList();

        foreach (var chunk in nonAddedSongIds.Chunk(chunkSize))
        {
            var chunkList = chunk.ToList();
            context.Set<SongArtist>()
                .Include(sa => sa.Artist)
                .Where(sa => chunkList.Contains(sa.SongId))
                .Load();
        }

        // Now sync all songs - their SongArtists collections are already populated via relationship fixup
        foreach (var song in songsToSync)
        {
            song.SyncDenormalizedFields();
        }

        // Same pattern for albums - batch-load AlbumArtists for all non-Added albums
        var nonAddedAlbumIds = albumsToSync
            .Where(a => context.Entry(a).State != EntityState.Added)
            .Select(a => a.Id)
            .ToList();

        foreach (var chunk in nonAddedAlbumIds.Chunk(chunkSize))
        {
            var chunkList = chunk.ToList();
            context.Set<AlbumArtist>()
                .Include(aa => aa.Artist)
                .Where(aa => chunkList.Contains(aa.AlbumId))
                .Load();
        }

        // Now sync all albums - their AlbumArtists collections are already populated
        foreach (var album in albumsToSync)
        {
            album.SyncDenormalizedFields();
        }
    }
}
