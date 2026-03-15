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

        // 1. Process Song changes — single pass to build ID→Entity map AND collect Added songs,
        //    avoiding redundant context.Entry() lookups in later filters.
        var trackedSongsById = new Dictionary<Guid, (Song Entity, EntityState State)>();
        foreach (var entry in context.ChangeTracker.Entries<Song>())
        {
            trackedSongsById[entry.Entity.Id] = (entry.Entity, entry.State);
            if (entry.State == EntityState.Added)
                songsToSync.Add(entry.Entity);
        }

        var songArtistEntries = context.ChangeTracker.Entries<SongArtist>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted);
        foreach (var sae in songArtistEntries)
        {
            if (trackedSongsById.TryGetValue(sae.Entity.SongId, out var info))
                songsToSync.Add(info.Entity);
        }

        // 2. Process Album changes — same single-pass pattern.
        var trackedAlbumsById = new Dictionary<Guid, (Album Entity, EntityState State)>();
        foreach (var entry in context.ChangeTracker.Entries<Album>())
        {
            trackedAlbumsById[entry.Entity.Id] = (entry.Entity, entry.State);
            if (entry.State == EntityState.Added)
                albumsToSync.Add(entry.Entity);
        }

        var albumArtistEntries = context.ChangeTracker.Entries<AlbumArtist>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted);
        foreach (var aae in albumArtistEntries)
        {
            if (trackedAlbumsById.TryGetValue(aae.Entity.AlbumId, out var info))
                albumsToSync.Add(info.Entity);
        }

        // 3. Batch-load relationships for all affected entities (fixes N+1 query problem)
        // Use cached state to avoid redundant context.Entry() calls in the filter.
        const int chunkSize = 500;

        // Batch-load SongArtists for all non-Added songs (Added songs already have artists in memory)
        var nonAddedSongIds = songsToSync
            .Where(s => trackedSongsById.TryGetValue(s.Id, out var info) && info.State != EntityState.Added)
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

        foreach (var song in songsToSync)
            song.SyncDenormalizedFields();

        // Same pattern for albums
        var nonAddedAlbumIds = albumsToSync
            .Where(a => trackedAlbumsById.TryGetValue(a.Id, out var info) && info.State != EntityState.Added)
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

        foreach (var album in albumsToSync)
        {
            var prevArtistName = album.ArtistName;
            var prevPrimaryArtistName = album.PrimaryArtistName;
            album.SyncDenormalizedFields();

            // AutoDetectChangesEnabled=false: scalar changes on Unchanged albums are not auto-detected.
            // Only mark the two properties that SyncDenormalizedFields touches, and only when they changed.
            var albumEntry = context.Entry(album);
            if (albumEntry.State == EntityState.Unchanged &&
                (album.ArtistName != prevArtistName || album.PrimaryArtistName != prevPrimaryArtistName))
            {
                albumEntry.Property(a => a.ArtistName).IsModified = true;
                albumEntry.Property(a => a.PrimaryArtistName).IsModified = true;
            }
        }
    }
}
