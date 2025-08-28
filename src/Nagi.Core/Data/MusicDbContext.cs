using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;

namespace Nagi.Core.Data;

/// <summary>
///     Represents the Entity Framework Core database context for the application's music library.
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

    /// <summary>
    ///     Configures the database model, including table relationships, indexes, and constraints.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure the Song entity.
        modelBuilder.Entity<Song>(entity =>
        {
            entity.Property(s => s.Title).UseCollation("NOCASE");
            entity.Property(s => s.FilePath).UseCollation("NOCASE");

            entity.HasIndex(s => s.Title);
            entity.HasIndex(s => s.FilePath).IsUnique();
            entity.HasIndex(s => s.ArtistId);
            entity.HasIndex(s => s.AlbumId);
            entity.HasIndex(s => s.FolderId);
            entity.HasIndex(s => s.LastPlayedDate);
            entity.HasIndex(s => s.DateAddedToLibrary);
            entity.HasIndex(s => s.PlayCount);
            entity.HasIndex(s => s.IsLoved);

            // Composite index for sorting tracks within an album.
            entity.HasIndex(s => new { s.AlbumId, s.DiscNumber, s.TrackNumber });

            entity.HasOne(s => s.Folder)
                .WithMany(f => f.Songs)
                .HasForeignKey(s => s.FolderId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(s => s.ListenHistory)
                .WithOne(lh => lh.Song)
                .HasForeignKey(lh => lh.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(s => s.Genres)
                .WithMany(g => g.Songs);
        });

        // Configure the Album entity.
        modelBuilder.Entity<Album>(entity =>
        {
            entity.Property(a => a.Title).UseCollation("NOCASE");
            entity.HasIndex(a => a.Title);
            entity.HasIndex(a => a.ArtistId);
            entity.HasIndex(a => a.Year);
            entity.HasIndex(a => new { a.Title, a.ArtistId }).IsUnique();
        });

        // Configure the Artist entity.
        modelBuilder.Entity<Artist>(entity =>
        {
            entity.Property(a => a.Name).UseCollation("NOCASE");
            entity.HasIndex(a => a.Name).IsUnique();
        });

        // Configure the Genre entity.
        modelBuilder.Entity<Genre>(entity =>
        {
            entity.Property(g => g.Name).UseCollation("NOCASE");
            entity.HasIndex(g => g.Name).IsUnique();
        });

        // Configure the ListenHistory entity.
        modelBuilder.Entity<ListenHistory>(entity =>
        {
            entity.HasIndex(lh => lh.SongId);
            entity.HasIndex(lh => lh.ListenTimestampUtc);
            entity.HasIndex(lh => lh.IsScrobbled);
        });

        // Configure the Folder entity.
        modelBuilder.Entity<Folder>(entity =>
        {
            entity.Property(f => f.Path).UseCollation("NOCASE");
            entity.HasIndex(f => f.Path).IsUnique();
        });

        // Configure the Playlist entity.
        modelBuilder.Entity<Playlist>(entity =>
        {
            entity.Property(p => p.Name).UseCollation("NOCASE");
            entity.HasIndex(p => p.Name).IsUnique();
        });

        // Configure the PlaylistSong join entity.
        modelBuilder.Entity<PlaylistSong>(entity =>
        {
            entity.HasKey(ps => new { ps.PlaylistId, ps.SongId });

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
    ///     Deletes and recreates the entire database. This is a destructive operation
    ///     intended for development, testing, or user-initiated library resets.
    /// </summary>
    /// <param name="logger">The logger to use for recording operation status.</param>
    public void RecreateDatabase(ILogger logger)
    {
        try
        {
            Database.EnsureDeleted();
        }
        catch (Exception ex)
        {
            // Log a warning if deletion fails, as the database might be locked by another process.
            logger.LogWarning(ex, "Failed to delete the database. It may be locked by another process.");
        }

        try
        {
            Database.EnsureCreated();
        }
        catch (Exception ex)
        {
            // This is a critical failure if the database cannot be created.
            logger.LogCritical(ex, "Failed to create the new database after deletion.");
            throw;
        }
    }
}