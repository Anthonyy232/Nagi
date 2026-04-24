using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nagi.Core.Models;

namespace Nagi.Core.Data.Interceptors;

/// <summary>
///     An EF Core interceptor that automatically synchronizes denormalized artist fields
///     (ArtistName and PrimaryArtistName) on Song and Album entities, and the sort key
///     (SortName / SortTitle / PrimaryArtistSortName) on Artist, Album, and Song entities,
///     before they are saved to the database.
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
        {
            var prevSortTitle = song.SortTitle;
            var prevPrimaryArtistSortName = song.PrimaryArtistSortName;
            var prevArtistName = song.ArtistName;
            var prevPrimaryArtistName = song.PrimaryArtistName;
            song.SyncDenormalizedFields();

            var songEntry = context.Entry(song);
            if (songEntry.State == EntityState.Unchanged)
            {
                if (song.ArtistName != prevArtistName || song.PrimaryArtistName != prevPrimaryArtistName)
                {
                    songEntry.Property(s => s.ArtistName).IsModified = true;
                    songEntry.Property(s => s.PrimaryArtistName).IsModified = true;
                }
                if (song.SortTitle != prevSortTitle)
                    songEntry.Property(s => s.SortTitle).IsModified = true;
                if (song.PrimaryArtistSortName != prevPrimaryArtistSortName)
                    songEntry.Property(s => s.PrimaryArtistSortName).IsModified = true;
            }
        }

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
            var prevSortTitle = album.SortTitle;
            var prevPrimaryArtistSortName = album.PrimaryArtistSortName;
            album.SyncDenormalizedFields();

            // AutoDetectChangesEnabled=false: scalar changes on Unchanged albums are not auto-detected.
            // Only mark properties that SyncDenormalizedFields touches, and only when they changed.
            var albumEntry = context.Entry(album);
            if (albumEntry.State == EntityState.Unchanged)
            {
                if (album.ArtistName != prevArtistName || album.PrimaryArtistName != prevPrimaryArtistName)
                {
                    albumEntry.Property(a => a.ArtistName).IsModified = true;
                    albumEntry.Property(a => a.PrimaryArtistName).IsModified = true;
                }
                if (album.SortTitle != prevSortTitle)
                    albumEntry.Property(a => a.SortTitle).IsModified = true;
                if (album.PrimaryArtistSortName != prevPrimaryArtistSortName)
                    albumEntry.Property(a => a.PrimaryArtistSortName).IsModified = true;
            }
        }

        // Sync Artist.SortName on Added artists and on Modified artists whose Name changed.
        // Legacy rows with empty SortName are backfilled once by App.BackfillSortKeysAsync.
        foreach (var entry in context.ChangeTracker.Entries<Artist>()
                     .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified))
        {
            if (entry.State == EntityState.Modified && !entry.Property(a => a.Name).IsModified)
                continue;

            var prev = entry.Entity.SortName;
            entry.Entity.SyncSortName();
            if (entry.State == EntityState.Modified && entry.Entity.SortName != prev)
                entry.Property(a => a.SortName).IsModified = true;
        }
    }
}
