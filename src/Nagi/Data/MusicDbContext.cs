using Microsoft.EntityFrameworkCore;
using Nagi.Models;
using System;
using System.Diagnostics;

namespace Nagi.Data;

/// <summary>
/// The Entity Framework Core database context for the application's music library.
/// </summary>
public class MusicDbContext : DbContext {
    public MusicDbContext(DbContextOptions<MusicDbContext> options) : base(options) {
    }

    public DbSet<Song> Songs { get; set; } = null!;
    public DbSet<Album> Albums { get; set; } = null!;
    public DbSet<Artist> Artists { get; set; } = null!;
    public DbSet<Folder> Folders { get; set; } = null!;
    public DbSet<Playlist> Playlists { get; set; } = null!;
    public DbSet<PlaylistSong> PlaylistSongs { get; set; } = null!;
    public DbSet<Genre> Genres { get; set; } = null!;
    public DbSet<ListenHistory> ListenHistory { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);

        // Configure Song entity
        modelBuilder.Entity<Song>(entity => {
            // Use case-insensitive collation for text-based lookups.
            entity.Property(s => s.Title).UseCollation("NOCASE");
            entity.Property(s => s.FilePath).UseCollation("NOCASE");

            // Define indexes for frequently queried columns.
            entity.HasIndex(s => s.Title);
            entity.HasIndex(s => s.FilePath).IsUnique();
            entity.HasIndex(s => s.ArtistId);
            entity.HasIndex(s => s.AlbumId);
            entity.HasIndex(s => s.FolderId);
            entity.HasIndex(s => s.LastPlayedDate);

            // Define relationships.
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

        // Configure Album entity
        modelBuilder.Entity<Album>(entity => {
            entity.Property(a => a.Title).UseCollation("NOCASE");
            entity.HasIndex(a => a.Title);
            entity.HasIndex(a => a.ArtistId);
            entity.HasIndex(a => new { a.Title, a.ArtistId }).IsUnique();
        });

        // Configure Artist entity
        modelBuilder.Entity<Artist>(entity => {
            entity.Property(a => a.Name).UseCollation("NOCASE");
            entity.HasIndex(a => a.Name).IsUnique();
        });

        // Configure Genre entity
        modelBuilder.Entity<Genre>(entity => {
            entity.Property(g => g.Name).UseCollation("NOCASE");
            entity.HasIndex(g => g.Name).IsUnique();
        });

        // Configure ListenHistory entity
        modelBuilder.Entity<ListenHistory>(entity => {
            entity.HasIndex(lh => lh.SongId);
            entity.HasIndex(lh => lh.ListenTimestampUtc);
        });

        // Configure Folder entity
        modelBuilder.Entity<Folder>(entity => {
            entity.Property(f => f.Path).UseCollation("NOCASE");
            entity.HasIndex(f => f.Path).IsUnique();
        });

        // Configure Playlist entity
        modelBuilder.Entity<Playlist>(entity => {
            entity.Property(p => p.Name).UseCollation("NOCASE");
            entity.HasIndex(p => p.Name).IsUnique();
        });

        // Configure PlaylistSong (many-to-many join) entity
        modelBuilder.Entity<PlaylistSong>(entity => {
            entity.HasKey(ps => new { ps.PlaylistId, ps.SongId });

            entity.HasOne(ps => ps.Playlist)
                .WithMany(p => p.PlaylistSongs)
                .HasForeignKey(ps => ps.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ps => ps.Song)
                .WithMany(s => s.PlaylistSongs)
                .HasForeignKey(ps => ps.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(ps => new { ps.PlaylistId, ps.Order });
        });
    }

    /// <summary>
    /// WARNING: This is a destructive operation that deletes and recreates the database.
    /// It should only be used for development, testing, or a user-initiated library reset.
    /// </summary>
    public void RecreateDatabase() {
        try {
            Database.EnsureDeleted();
        }
        catch (Exception ex) {
            // Log if deletion fails, as the database might be locked.
            Debug.WriteLine($"[MusicDbContext] Warning: Failed to delete database. It may be in use. Error: {ex.Message}");
        }
        try {
            Database.EnsureCreated();
        }
        catch (Exception ex) {
            // This is a critical failure if the database cannot be created.
            Debug.WriteLine($"[MusicDbContext] CRITICAL: Failed to create new database. Error: {ex.Message}");
            throw;
        }
    }
}