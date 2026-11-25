using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Nagi.Core.Data;
using Nagi.Core.Library;
using Nagi.Core.MusicServers;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Tests.Utils;
using Xunit;

namespace Nagi.Core.Tests.Data;

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

    [Fact]
    public async Task RecheckIncompleteArtistMetadata_ClearsTheCheckOnlyForArtistsMissingABioOrImage()
    {
        await using var context = new MusicDbContext(
            new DbContextOptionsBuilder<MusicDbContext>().UseSqlite("Data Source=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20261024222916_AddSongReleaseGroupId");
        var checkedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Raw SQL, because the current model has columns this schema doesn't.
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO Artists (Id, Name, SortName, Biography, LocalImageCachePath, MetadataLastCheckedUtc) VALUES
                ({Guid.NewGuid()}, 'Complete', 'Complete', 'Bio', 'image.jpg', {checkedAt}),
                ({Guid.NewGuid()}, 'No bio', 'No bio', NULL, 'image.jpg', {checkedAt}),
                ({Guid.NewGuid()}, 'No image', 'No image', 'Bio', NULL, {checkedAt});
            """);

        await context.Database.MigrateAsync();

        (await context.Artists.ToDictionaryAsync(a => a.Name, a => a.MetadataLastCheckedUtc)).Should().Equal(
            new Dictionary<string, DateTime?> { ["Complete"] = checkedAt, ["No bio"] = null, ["No image"] = null });
    }

    [Fact]
    public async Task StripLyricsLanguagePrefix_RemovesOnlyALeadingLanguageCode()
    {
        await using var context = new MusicDbContext(
            new DbContextOptionsBuilder<MusicDbContext>().UseSqlite("Data Source=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20261101215552_AddServerPlaylists");
        var folderId = await AddFolderAsync(context);
        string[] lyrics = ["eng||Words", "EN||Words", "Words || more", "[00:01.00]eng||Words"];
        for (var i = 0; i < lyrics.Length; i++) await AddSongAsync(context, folderId, $"{i}", lyrics[i]);

        await context.Database.MigrateAsync();

        (await context.Songs.OrderBy(s => s.Title).Select(s => s.Lyrics).ToListAsync())
            .Should().Equal("Words", "Words", "Words || more", "[00:01.00]eng||Words");
    }

    [Fact]
    public async Task RecordBiographySource_MovesTheWikipediaCreditOutOfStoredBiographiesInAnyLanguage()
    {
        await using var context = new MusicDbContext(
            new DbContextOptionsBuilder<MusicDbContext>().UseSqlite("Data Source=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20261107031219_StripLyricsLanguagePrefix");
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO Artists (Id, Name, SortName, Biography) VALUES
                ('00000000-0000-0000-0000-000000000001', 'German', 'German',
                 'Eine Band.' || char(10) || char(10) || 'Aus Wikipedia, unter der Lizenz CC BY-SA.'),
                ('00000000-0000-0000-0000-000000000002', 'Turkish', 'Turkish',
                 'Bir grup.' || char(10) || char(10) || 'Vikipedi''den, CC BY-SA lisansı altında.'),
                ('00000000-0000-0000-0000-000000000003', 'Other', 'Other', 'A band, from Wikipedia, under the CC BY-SA license.');
            """);

        await context.Database.MigrateAsync();

        (await context.Artists.OrderBy(a => a.Name).Select(a => new { a.Biography, a.BiographySource }).ToListAsync())
            .Should().Equal(
                new { Biography = (string?)"Eine Band.", BiographySource = (string?)"wikipedia" },
                new { Biography = (string?)"A band, from Wikipedia, under the CC BY-SA license.", BiographySource = (string?)null },
                new { Biography = (string?)"Bir grup.", BiographySource = (string?)"wikipedia" });

        await context.GetService<IMigrator>().MigrateAsync("20261107031219_StripLyricsLanguagePrefix");

        (await context.Database.SqlQueryRaw<string>("SELECT Biography AS Value FROM Artists WHERE Name = 'German'").SingleAsync())
            .Should().Be("Eine Band.\n\nFrom Wikipedia, under the CC BY-SA license.");
    }

    [Fact]
    public async Task RemapSmartPlaylistSortOrder_KeepsEachPlaylistsSort()
    {
        await using var context = new MusicDbContext(
            new DbContextOptionsBuilder<MusicDbContext>().UseSqlite("Data Source=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20261107031219_StripLyricsLanguagePrefix");
        // The numbers the old smart playlist sort stored for these sorts.
        var saved = new Dictionary<string, int>
        {
            ["Title"] = 0,
            ["Artist"] = 2,
            ["Album desc"] = 5,
            ["Year"] = 6,
            ["Date added desc"] = 13,
            ["Track number"] = 14,
            ["Duration"] = 16,
            ["Random"] = 22
        };
        foreach (var (name, sortOrder) in saved)
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO SmartPlaylists (Id, Name, DateCreated, DateModified, MatchAllRules, SortOrder)
                VALUES ({Guid.NewGuid()}, {name}, '2026-01-01', '2026-01-01', 1, {sortOrder})
                """);

        await context.Database.MigrateAsync();

        (await context.SmartPlaylists.ToDictionaryAsync(p => p.Name, p => p.SortOrder)).Should().Equal(
            new Dictionary<string, SongSortOrder>
            {
                ["Title"] = SongSortOrder.TitleAsc,
                ["Artist"] = SongSortOrder.ArtistAsc,
                ["Album desc"] = SongSortOrder.AlbumDesc,
                ["Year"] = SongSortOrder.YearAsc,
                ["Date added desc"] = SongSortOrder.DateAddedDesc,
                ["Track number"] = SongSortOrder.TrackNumberAsc,
                ["Duration"] = SongSortOrder.DurationAsc,
                ["Random"] = SongSortOrder.Random
            });
    }

    [Fact]
    public async Task StoreEnumsByName_KeepsEveryStoredEnumAndGoesBack()
    {
        await using var context = new MusicDbContext(
            new DbContextOptionsBuilder<MusicDbContext>().UseSqlite("Data Source=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        await context.GetService<IMigrator>().MigrateAsync("20261124025217_AddArtistSortTags");
        var folderId = await AddFolderAsync(context);
        var songId = await AddSongAsync(context, folderId, "Song");
        var playlistId = Guid.NewGuid();
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO SmartPlaylists (Id, Name, DateCreated, DateModified, MatchAllRules, SortOrder)
            VALUES ({playlistId}, 'Recent', '2026-01-01', '2026-01-01', 1, 25);
            INSERT INTO SmartPlaylistRules (Id, SmartPlaylistId, Field, Operator, "Order")
            VALUES ({Guid.NewGuid()}, {playlistId}, 22, 13, 0);
            INSERT INTO MusicServers (FolderId, Kind, BaseUrl, UserName, UserId, UsesApiKey)
            VALUES ({folderId}, 1, 'https://jellyfin.example', 'me', 'id', 0);
            INSERT INTO ListenHistory (SongId, ListenTimestampUtc, EndReason, ContextType, ListenDurationTicks,
                IsEligibleForScrobbling, IsScrobbled, IsSubmittedToListenBrainz, IsSubmittedToServer)
            VALUES ({songId}, '2026-01-01', 2, 8, 0, 0, 0, 0, 0);
            """);

        await context.Database.MigrateAsync();

        (await context.SmartPlaylists.SingleAsync()).SortOrder.Should().Be(SongSortOrder.FileModifiedDateDesc);
        var rule = await context.SmartPlaylistRules.SingleAsync();
        (rule.Field, rule.Operator).Should().Be((SmartPlaylistField.FileModifiedDate, SmartPlaylistOperator.IsInTheLast));
        (await context.MusicServers.SingleAsync()).Kind.Should().Be(MusicServerKind.Jellyfin);
        var listen = await context.ListenHistory.SingleAsync();
        (listen.EndReason, listen.ContextType).Should().Be((PlaybackEndReason.PausedAndAbandoned, PlaybackContextType.Transient));

        await context.GetService<IMigrator>().MigrateAsync("20261124025217_AddArtistSortTags");

        (await context.Database.SqlQueryRaw<string>("""
            SELECT SortOrder || ' ' || Field || ' ' || Operator || ' ' || Kind || ' ' || EndReason || ' ' || ContextType AS Value
            FROM SmartPlaylists, SmartPlaylistRules, MusicServers, ListenHistory
            """).SingleAsync()).Should().Be("25 22 13 1 2 8");
    }

    // Rows go in as SQL: the model has columns an older schema lacks.
    private static async Task<Guid> AddFolderAsync(MusicDbContext context)
    {
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlAsync($"INSERT INTO Folders (Id, Name, Path) VALUES ({id}, 'Music', 'Music')");
        return id;
    }

    private static async Task<Guid> AddSongAsync(MusicDbContext context, Guid folderId, string title, string? lyrics = null)
    {
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO Songs (Id, Title, SortTitle, ArtistName, PrimaryArtistName, PrimaryArtistSortName, FolderId, FilePath,
                DirectoryPath, DurationTicks, IsLoved, IsLovedSyncPending, PlayCount, SkipCount, TotalListenTimeTicks, Lyrics)
            VALUES ({id}, {title}, {title}, '', '', '', {folderId}, {title}, '', 0, 0, 0, 0, 0, 0, {lyrics});
            """);
        return id;
    }
}
