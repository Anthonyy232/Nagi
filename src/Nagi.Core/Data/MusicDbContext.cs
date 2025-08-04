using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Models;

namespace Nagi.Core.Data;

/// <summary>
///     The Entity Framework Core database context for the application's music library.
/// </summary>
public class MusicDbContext : DbContext
{
    public MusicDbContext(DbContextOptions<MusicDbContext> options) : base(options)
    {
    }

    public DbSet<Song> Songs { get; set; } = null!;
    public DbSet<Album> Albums { get; set; } = null!;
    public DbSet<Artist> Artists { get; set; } = null!;
    public DbSet<Folder> Folders { get; set; } = null!;
    public DbSet<Playlist> Playlists { get; set; } = null!;
    public DbSet<PlaylistSong> PlaylistSongs { get; set; } = null!;
    public DbSet<Genre> Genres { get; set; } = null!;
    public DbSet<ListenHistory> ListenHistory { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Song>(entity =>
        {
            // Use case-insensitive collation for efficient, case-insensitive text lookups.
            entity.Property(s => s.Title).UseCollation("NOCASE");
            entity.Property(s => s.FilePath).UseCollation("NOCASE");

            // Define indexes for frequently queried columns to improve performance.
            entity.HasIndex(s => s.Title);
            entity.HasIndex(s => s.FilePath).IsUnique();
            entity.HasIndex(s => s.ArtistId);
            entity.HasIndex(s => s.AlbumId);
            entity.HasIndex(s => s.FolderId);
            entity.HasIndex(s => s.LastPlayedDate);
            entity.HasIndex(s => s.DateAddedToLibrary);
            entity.HasIndex(s => s.PlayCount);
            entity.HasIndex(s => s.IsLoved);

            // Composite index for fast, correct sorting of tracks within an album.
            entity.HasIndex(s => new { s.AlbumId, s.DiscNumber, s.TrackNumber });

            // Prevent deleting a folder if it still contains songs.
            entity.HasOne(s => s.Folder)
                .WithMany(f => f.Songs)
                .HasForeignKey(s => s.FolderId)
                .OnDelete(DeleteBehavior.Restrict);

            // Cascade delete listen history when a song is deleted.
            entity.HasMany(s => s.ListenHistory)
                .WithOne(lh => lh.Song)
                .HasForeignKey(lh => lh.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(s => s.Genres)
                .WithMany(g => g.Songs);
        });

        modelBuilder.Entity<Album>(entity =>
        {
            entity.Property(a => a.Title).UseCollation("NOCASE");
            entity.HasIndex(a => a.Title);
            entity.HasIndex(a => a.ArtistId);
            entity.HasIndex(a => a.Year);

            // Ensure album titles are unique per artist.
            entity.HasIndex(a => new { a.Title, a.ArtistId }).IsUnique();
        });

        modelBuilder.Entity<Artist>(entity =>
        {
            entity.Property(a => a.Name).UseCollation("NOCASE");
            entity.HasIndex(a => a.Name).IsUnique();
        });

        modelBuilder.Entity<Genre>(entity =>
        {
            entity.Property(g => g.Name).UseCollation("NOCASE");
            entity.HasIndex(g => g.Name).IsUnique();
        });

        modelBuilder.Entity<ListenHistory>(entity =>
        {
            entity.HasIndex(lh => lh.SongId);
            entity.HasIndex(lh => lh.ListenTimestampUtc);
            entity.HasIndex(lh => lh.IsScrobbled);
        });

        modelBuilder.Entity<Folder>(entity =>
        {
            entity.Property(f => f.Path).UseCollation("NOCASE");
            entity.HasIndex(f => f.Path).IsUnique();
        });

        modelBuilder.Entity<Playlist>(entity =>
        {
            entity.Property(p => p.Name).UseCollation("NOCASE");
            entity.HasIndex(p => p.Name).IsUnique();
        });

        modelBuilder.Entity<PlaylistSong>(entity =>
        {
            // Define the composite primary key for the join table.
            entity.HasKey(ps => new { ps.PlaylistId, ps.SongId });

            // Cascade delete join entries when a playlist or song is deleted.
            entity.HasOne(ps => ps.Playlist)
                .WithMany(p => p.PlaylistSongs)
                .HasForeignKey(ps => ps.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);


            entity.HasOne(ps => ps.Song)
                .WithMany(s => s.PlaylistSongs)
                .HasForeignKey(ps => ps.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            // Index for efficient, ordered retrieval of songs in a playlist.
            entity.HasIndex(ps => new { ps.PlaylistId, ps.Order });
        });
    }

    /// <summary>
    ///     Deletes and recreates the database.
    ///     This is a destructive operation intended for development, testing, or user-initiated library resets.
    /// </summary>
    public void RecreateDatabase()
    {
        try
        {
            Database.EnsureDeleted();
        }
        catch (Exception ex)
        {
            // Log if deletion fails, as the database might be locked by another process.
            Debug.WriteLine($"Warning: Failed to delete database. It may be in use. Error: {ex.Message}");
        }

        try
        {
            Database.EnsureCreated();
        }
        catch (Exception ex)
        {
            // This is a critical failure if the database cannot be created.
            Debug.WriteLine($"CRITICAL: Failed to create new database. Error: {ex.Message}");
            throw;
        }
    }
}