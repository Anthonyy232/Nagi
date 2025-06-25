using System;
using System.Diagnostics;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Nagi.Models;

namespace Nagi.Data;

/// <summary>
///     Manages the database session for the application, allowing querying and saving of data.
/// </summary>
public class MusicDbContext : DbContext
{
    /// <summary>
    ///     This constructor is used by the application to connect to the default SQLite database.
    /// </summary>
    public MusicDbContext()
    {
        var folder = Environment.SpecialFolder.LocalApplicationData;
        var path = Environment.GetFolderPath(folder);
        DbPath = Path.Join(path, "local_music_app.db");
    }

    /// <summary>
    ///     This constructor is used for dependency injection and unit testing,
    ///     allowing the configuration (like using an in-memory database) to be passed in.
    /// </summary>
    /// <param name="options">The options for this context.</param>
    public MusicDbContext(DbContextOptions<MusicDbContext> options) : base(options)
    {
        // When options are passed in, the DbPath is not determined by this constructor.
        // We can leave it empty or get it from the options if needed, but it's not required here.
        DbPath = string.Empty;
    }

    public DbSet<Song> Songs { get; set; } = null!;
    public DbSet<Album> Albums { get; set; } = null!;
    public DbSet<Artist> Artists { get; set; } = null!;
    public DbSet<Folder> Folders { get; set; } = null!;
    public DbSet<Playlist> Playlists { get; set; } = null!;
    public DbSet<PlaylistSong> PlaylistSongs { get; set; } = null!;

    /// <summary>
    ///     The full path to the SQLite database file.
    /// </summary>
    public string DbPath { get; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // This ensures that the context is configured to use the SQLite database
        // ONLY if it hasn't already been configured elsewhere (e.g., by the unit tests).
        if (!options.IsConfigured)
            options.UseSqlite($"Data Source={DbPath}")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .EnableSensitiveDataLogging(false)
                .EnableDetailedErrors(false);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Song entity configuration
        modelBuilder.Entity<Song>(entity =>
        {
            entity.Property(s => s.Title).UseCollation("NOCASE");
            entity.Property(s => s.FilePath).UseCollation("NOCASE");

            entity.HasIndex(s => s.Title);
            entity.HasIndex(s => s.FilePath).IsUnique();
            entity.HasIndex(s => s.ArtistId);
            entity.HasIndex(s => s.AlbumId);
            entity.HasIndex(s => s.FolderId);

            entity.HasOne(s => s.Folder)
                .WithMany(f => f.Songs)
                .HasForeignKey(s => s.FolderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Album entity configuration
        modelBuilder.Entity<Album>(entity =>
        {
            entity.Property(a => a.Title).UseCollation("NOCASE");

            entity.HasIndex(a => a.Title);
            entity.HasIndex(a => new { a.Title, a.ArtistId }).IsUnique();
            entity.HasIndex(a => a.ArtistId);
        });

        // Artist entity configuration
        modelBuilder.Entity<Artist>(entity =>
        {
            entity.Property(a => a.Name).UseCollation("NOCASE");
            entity.HasIndex(a => a.Name).IsUnique();
        });

        // Folder entity configuration
        modelBuilder.Entity<Folder>(entity =>
        {
            entity.Property(f => f.Path).UseCollation("NOCASE");
            entity.HasIndex(f => f.Path).IsUnique();
        });

        // Playlist entity configuration
        modelBuilder.Entity<Playlist>(entity =>
        {
            entity.Property(p => p.Name).UseCollation("NOCASE");
            entity.HasIndex(p => p.Name).IsUnique();
        });

        // PlaylistSong join entity configuration
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

            entity.HasIndex(ps => new { ps.PlaylistId, ps.Order });
        });
    }

    /// <summary>
    ///     Deletes the existing database file and recreates it based on the current model.
    ///     This is a destructive operation intended for development and should not be used in production.
    ///     For production schema changes, use EF Core Migrations.
    /// </summary>
    public void RecreateDatabase()
    {
        try
        {
            // EnsureDeleted returns false if the database does not exist, so no prior check is needed.
            Database.EnsureDeleted();
        }
        catch (Exception ex)
        {
            // Log a warning if deletion fails, as the file might be locked.
            // This is not critical, as creation might still succeed or fail with a more specific error.
            Debug.WriteLine(
                $"[MusicDbContext] Warning: Failed to delete database. It may be in use. Error: {ex.Message}");
        }

        try
        {
            // Creates the database and schema. Throws if the database exists but is not compatible.
            Database.EnsureCreated();
        }
        catch (Exception ex)
        {
            // This is a critical failure. Log the error and re-throw to alert the caller.
            Debug.WriteLine($"[MusicDbContext] CRITICAL: Failed to create new database. Error: {ex.Message}");
            throw;
        }
    }
}