using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Tests.Utils;
using Xunit;

namespace Nagi.Core.Tests;

public class DatabaseMigrationTests
{
    [Fact]
    public async Task Migrate_FromPreviouslyAppliedOrderRemoval_DoesNotRequireBackupTable()
    {
        await using var context = new MusicDbContext(
            new DbContextOptionsBuilder<MusicDbContext>().UseSqlite("Data Source=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20260112021339_FoldersTable");
        await context.Database.ExecuteSqlRawAsync("DROP TABLE __NagiPlaylistOrderBackup;");

        await context.Database.MigrateAsync();

        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE name = '__NagiPlaylistOrderBackup'").SingleAsync())
            .Should().Be(0);
    }

    [Fact]
    public async Task DowngradeMultiArtist_PreservesPrimaryArtistAndResolvesMissingAlbumArtist()
    {
        await using var context = new MusicDbContext(
            new DbContextOptionsBuilder<MusicDbContext>().UseSqlite("Data Source=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.Database.MigrateAsync();
        var primary = new Artist { Name = "Primary" };
        var album = new Album { Title = "Collaboration" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = new Artist { Name = "Guest" }, Order = 1 });
        album.AlbumArtists.Add(new AlbumArtist { Artist = primary, Order = 0 });
        context.Albums.AddRange(album, new Album { Title = "Missing artist" });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.GetService<IMigrator>().MigrateAsync("20260115065116_AddMultiArtistAndAlbumArtist");

        (await context.Database.SqlQueryRaw<Guid>(
            "SELECT ArtistId AS Value FROM Albums WHERE Title = 'Collaboration'").SingleAsync()).Should().Be(primary.Id);
        var fallbackId = await context.Database.SqlQueryRaw<Guid>(
            "SELECT ArtistId AS Value FROM Albums WHERE Title = 'Missing artist'").SingleAsync();
        (await context.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM Artists WHERE Id = {fallbackId}").SingleAsync())
            .Should().Be(1);

        await context.Database.MigrateAsync();
        (await context.AlbumArtists.Where(a => a.AlbumId == album.Id).SingleAsync()).ArtistId.Should().Be(primary.Id);
    }

    [Fact]
    public async Task Migrate_FromOriginalSchema_PreservesLibraryData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var context = new MusicDbContext(
            new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20250723185305_InitialCreate");
        var artistId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var duration = TimeSpan.FromDays(2) + TimeSpan.FromTicks(123456789);
        var now = DateTime.UtcNow;
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO Artists (Id, Name) VALUES ({artistId}, 'The Artist');
            INSERT INTO Albums (Id, Title, ArtistId) VALUES ({albumId}, 'Album', {artistId});
            INSERT INTO Folders (Id, Name, Path) VALUES ({folderId}, 'Music', 'C:\Music');
            INSERT INTO Playlists (Id, Name, DateCreated, DateModified)
                VALUES ({playlistId}, 'Favorites', {now}, {now});
            INSERT INTO Songs (Id, Title, ArtistId, AlbumId, FolderId, Duration, FilePath, IsLoved, PlayCount, SkipCount)
                VALUES ({songId}, 'Song', {artistId}, {albumId}, {folderId}, {duration}, 'C:\Music\song.mp3', 1, 7, 3);
            INSERT INTO PlaylistSongs (PlaylistId, SongId, "Order") VALUES ({playlistId}, {songId}, 17);
            """);

        await context.Database.MigrateAsync();

        var song = await context.Songs.Include(s => s.SongArtists).SingleAsync();
        song.Duration.Should().Be(duration);
        song.DirectoryPath.Should().Be("C:\\Music");
        song.PlayCount.Should().Be(7);
        song.SkipCount.Should().Be(3);
        song.IsLoved.Should().BeTrue();
        song.SongArtists.Should().ContainSingle().Which.ArtistId.Should().Be(artistId);
        (await context.PlaylistSongs.SingleAsync()).Order.Should().Be(17);
        (await context.AlbumArtists.SingleAsync()).ArtistId.Should().Be(artistId);

        context.ChangeTracker.Clear();
        await context.GetService<IMigrator>().MigrateAsync("20250723185305_InitialCreate");
        (await context.Database.SqlQueryRaw<TimeSpan>("SELECT Duration AS Value FROM Songs").SingleAsync())
            .Should().Be(duration);
        (await context.Database.SqlQueryRaw<Guid>("SELECT ArtistId AS Value FROM Albums").SingleAsync())
            .Should().Be(artistId);
        (await context.Database.SqlQueryRaw<int>("SELECT \"Order\" AS Value FROM PlaylistSongs").SingleAsync())
            .Should().Be(17);

        await context.Database.MigrateAsync();
        (await context.Songs.SingleAsync()).Duration.Should().Be(duration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackfillSortKeys_HandlesBlankValuesAndLargeLibrariesWithoutTracking(bool useFileDatabase)
    {
        using var database = new DbContextFactoryTestHelper(useFileDatabase);
        await using var context = database.ContextFactory.CreateDbContext();
        var artist = new Artist { Name = "The Ärtist" };
        var album = new Album { Title = "An Étude" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = artist });
        var folder = new Folder { Name = "Music", Path = "C:\\Music" };
        context.Artists.Add(new Artist { Name = "" });
        context.Albums.Add(new Album { Title = "" });
        for (var index = 0; index < 501; index++)
        {
            var song = new Song
            {
                Title = index == 0 ? "" : $"The Élan {index}",
                FilePath = $"C:\\Music\\{index}.mp3",
                DirectoryPath = "C:\\Music",
                Album = album,
                Folder = folder
            };
            song.SongArtists.Add(new SongArtist { Artist = artist });
            context.Songs.Add(song);
        }
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("""
            UPDATE Artists SET SortName = '';
            UPDATE Albums SET SortTitle = '', PrimaryArtistSortName = '';
            UPDATE Songs SET SortTitle = '', PrimaryArtistSortName = '';
            """);
        context.ChangeTracker.Clear();

        await context.BackfillSortKeysAsync().WaitAsync(TimeSpan.FromSeconds(5));

        context.ChangeTracker.Entries().Should().BeEmpty();
        (await context.Artists.AsNoTracking().SingleAsync(a => a.Name == "The Ärtist")).SortName.Should().Be("ärtist");
        (await context.Artists.AsNoTracking().SingleAsync(a => a.Name == "")).SortName.Should().BeEmpty();
        var updatedAlbum = await context.Albums.AsNoTracking().SingleAsync(a => a.Id == album.Id);
        updatedAlbum.SortTitle.Should().Be("étude");
        updatedAlbum.PrimaryArtistSortName.Should().Be("ärtist");
        (await context.Songs.CountAsync(s => s.PrimaryArtistSortName == "ärtist")).Should().Be(501);
        (await context.Songs.CountAsync(s => s.SortTitle.StartsWith("élan "))).Should().Be(500);
        (await context.Songs.AsNoTracking().SingleAsync(s => s.Title == "")).SortTitle.Should().BeEmpty();

        await context.BackfillSortKeysAsync().WaitAsync(TimeSpan.FromSeconds(5));
        (await context.Songs.CountAsync()).Should().Be(501);
    }

    [Fact]
    public async Task Migrate_FromEmptyDatabase_CreatesTheCurrentSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options;
        await using var context = new MusicDbContext(options);

        await context.Database.OpenConnectionAsync();
        context.Database.HasPendingModelChanges().Should().BeFalse();
        await context.Database.MigrateAsync();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var sql = context.GetService<ISqlGenerationHelper>();
        foreach (var table in model.Tables)
        {
            await using var command = connection.CreateCommand();
            var tableName = sql.DelimitIdentifier(table.Name);
            var columns = string.Join(", ", table.Columns.Select(c => $"{tableName}.{sql.DelimitIdentifier(c.Name)}"));
            command.CommandText = $"SELECT {columns} FROM {tableName} LIMIT 0";
            await using var reader = await command.ExecuteReaderAsync();
        }
    }
}
