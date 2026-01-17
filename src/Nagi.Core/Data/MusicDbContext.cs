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

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.AddInterceptors(new Data.Interceptors.DenormalizationInterceptor());
    }

    public DbSet<Song> Songs { get; set; } = null!;
    public DbSet<Album> Albums { get; set; } = null!;
    public DbSet<Artist> Artists { get; set; } = null!;
    public DbSet<Folder> Folders { get; set; } = null!;
    public DbSet<Playlist> Playlists { get; set; } = null!;
    public DbSet<PlaylistSong> PlaylistSongs { get; set; } = null!;
    public DbSet<SongArtist> SongArtists { get; set; } = null!;
    public DbSet<AlbumArtist> AlbumArtists { get; set; } = null!;
    public DbSet<Genre> Genres { get; set; } = null!;
    public DbSet<ListenHistory> ListenHistory { get; set; } = null!;
    public DbSet<SmartPlaylist> SmartPlaylists { get; set; } = null!;
    public DbSet<SmartPlaylistRule> SmartPlaylistRules { get; set; } = null!;

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
            entity.Property(s => s.DirectoryPath).UseCollation("NOCASE");

            entity.HasIndex(s => s.Title);
            entity.HasIndex(s => s.ArtistName);
            entity.HasIndex(s => s.PrimaryArtistName);
            entity.HasIndex(s => s.FilePath).IsUnique();
            entity.HasIndex(s => s.DirectoryPath);
            entity.HasIndex(s => s.AlbumId);
            entity.HasIndex(s => s.FolderId);
            entity.HasIndex(s => s.DateAddedToLibrary);
            entity.HasIndex(s => s.LastPlayedDate);
            entity.HasIndex(s => s.PlayCount);
            entity.HasIndex(s => s.IsLoved);
            entity.HasIndex(s => s.Year);

            // Composite index for sorting tracks within an album.
            entity.HasIndex(s => new { s.AlbumId, s.DiscNumber, s.TrackNumber });

            // Composite index for efficient folder + directory queries.
            entity.HasIndex(s => new { s.FolderId, s.DirectoryPath });

            entity.HasOne(s => s.Folder)
                .WithMany(f => f.Songs)
                .HasForeignKey(s => s.FolderId)
                .OnDelete(DeleteBehavior.NoAction);

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
            entity.HasIndex(a => a.ArtistName);
            entity.HasIndex(a => a.PrimaryArtistName);
            entity.HasIndex(a => a.Year);
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
            entity.HasIndex(f => f.ParentFolderId);

            // Self-referencing relationship for folder hierarchy
            entity.HasOne(f => f.ParentFolder)
                .WithMany(f => f.SubFolders)
                .HasForeignKey(f => f.ParentFolderId)
                .OnDelete(DeleteBehavior.Cascade);
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

            entity.HasIndex(ps => new { ps.PlaylistId, ps.Order });
        });

        // Configure the SmartPlaylist entity.
        modelBuilder.Entity<SmartPlaylist>(entity =>
        {
            entity.Property(sp => sp.Name).UseCollation("NOCASE");
            entity.HasIndex(sp => sp.Name).IsUnique();
        });

        // Configure the SmartPlaylistRule entity.
        modelBuilder.Entity<SmartPlaylistRule>(entity =>
        {
            entity.HasOne(r => r.SmartPlaylist)
                .WithMany(sp => sp.Rules)
                .HasForeignKey(r => r.SmartPlaylistId)
                .OnDelete(DeleteBehavior.Cascade);

            // Index for efficient, ordered retrieval of rules in a smart playlist.
            entity.HasIndex(r => new { r.SmartPlaylistId, r.Order });
        });

        // Configure the SongArtist join entity for multi-artist support.
        modelBuilder.Entity<SongArtist>(entity =>
        {
            entity.HasKey(sa => new { sa.SongId, sa.ArtistId });

            entity.HasOne(sa => sa.Song)
                .WithMany(s => s.SongArtists)
                .HasForeignKey(sa => sa.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(sa => sa.Artist)
                .WithMany(a => a.SongArtists)
                .HasForeignKey(sa => sa.ArtistId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(sa => new { sa.SongId, sa.Order });
            entity.HasIndex(sa => sa.ArtistId);
        });

        // Configure the AlbumArtist join entity for multi-artist support.
        modelBuilder.Entity<AlbumArtist>(entity =>
        {
            entity.HasKey(aa => new { aa.AlbumId, aa.ArtistId });

            entity.HasOne(aa => aa.Album)
                .WithMany(a => a.AlbumArtists)
                .HasForeignKey(aa => aa.AlbumId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(aa => aa.Artist)
                .WithMany(a => a.AlbumArtists)
                .HasForeignKey(aa => aa.ArtistId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(aa => new { aa.AlbumId, aa.Order });
            entity.HasIndex(aa => aa.ArtistId);
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