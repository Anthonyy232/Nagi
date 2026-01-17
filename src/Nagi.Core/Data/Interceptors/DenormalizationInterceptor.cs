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

        // 3. Perform the synchronization on the collected entities
        // 3. Perform the synchronization on the collected entities
        foreach (var song in songsToSync)
        {
            // Critical: Ensure relationships are loaded before syncing.
            // If the Song was modified but SongArtists weren't included in the query,
            // SyncDenormalizedFields would see an empty list and wipe the data.
            if (context.Entry(song).State != EntityState.Added)
            {
                context.Entry(song).Collection(s => s.SongArtists).Query().Include(sa => sa.Artist).Load();
            }
            song.SyncDenormalizedFields();
        }

        foreach (var album in albumsToSync)
        {
            if (context.Entry(album).State != EntityState.Added)
            {
                context.Entry(album).Collection(a => a.AlbumArtists).Query().Include(aa => aa.Artist).Load();
            }
            album.SyncDenormalizedFields();
        }
    }
}
