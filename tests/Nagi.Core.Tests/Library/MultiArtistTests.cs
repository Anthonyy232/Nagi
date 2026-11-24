using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Library;

public class MultiArtistTests : LibraryTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RescanFolderForMusicAsync_WithMultipleArtists_CorrectlyPopulatesDenormalizedFields(bool hasAlbumArtists)
    {

        var folderId = Guid.NewGuid();
        var folderPath = "C:\\Music\\MultiArtist";
        var filePath = Path.Combine(folderPath, "song.mp3");

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(new Folder { Id = folderId, Path = folderPath, Name = "MultiArtist" });
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folderPath).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folderPath, "*.*", SearchOption.AllDirectories).Returns(new[] { (filePath, DateTime.UtcNow) });

        var metadata = new SongFileMetadata
        {
            FilePath = filePath,
            Title = "Collaboration Song",
            Artists = new List<string> { "Primary Artist", "Featured Artist" },
            AlbumArtists = hasAlbumArtists ? ["Primary Artist"] : [],
            Album = "Collab Album",
            Duration = TimeSpan.FromMinutes(3)
        };

        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(metadata);

        var result = await _library.Folders.RescanFolderForMusicAsync(folderId);

        result.Should().BeTrue("the scan should complete without errors");

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs
                .Include(s => s.SongArtists)
                .ThenInclude(sa => sa.Artist)
                .Include(s => s.Album)
                .ThenInclude(a => a!.AlbumArtists)
                .ThenInclude(aa => aa.Artist)
                .FirstOrDefaultAsync(s => s.FilePath == filePath);

            song.Should().NotBeNull();

            song!.ArtistName.Should().Be("Primary Artist & Featured Artist");
            song.PrimaryArtistName.Should().Be("Primary Artist");

            song.Album.Should().NotBeNull();
            song.Album!.ArtistName.Should().Be(hasAlbumArtists ? "Primary Artist" : "Primary Artist & Featured Artist");
            song.Album.PrimaryArtistName.Should().Be("Primary Artist");
        }
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_FillsMissingArtistIdsFromTagsOnly()
    {
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.AddRange(new Artist { Name = "Known", MusicBrainzId = "known-id" }, new Artist { Name = "Unidentified" });
            await context.SaveChangesAsync();
        }
        var pairedPath = @"C:\Music\paired.mp3";
        var unpairedPath = @"C:\Music\unpaired.mp3";
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (pairedPath, DateTime.UtcNow), (unpairedPath, DateTime.UtcNow) });
        _metadataService.ExtractMetadataAsync(pairedPath, folder.Path).Returns(new SongFileMetadata
        {
            FilePath = pairedPath,
            Title = "Paired",
            Artists = ["Known", "Unidentified", "New"],
            MusicBrainzArtistIds = ["tag-known", "tag-unidentified", "tag-new"]
        });
        _metadataService.ExtractMetadataAsync(unpairedPath, folder.Path).Returns(new SongFileMetadata
        {
            FilePath = unpairedPath,
            Title = "Unpaired",
            Artists = ["Duo A", "Duo B"],
            MusicBrainzArtistIds = ["tag-duo"]
        });

        await _library.Folders.RescanFolderForMusicAsync(folder.Id);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var ids = await assertContext.Artists.ToDictionaryAsync(a => a.Name, a => a.MusicBrainzId);
        ids["Known"].Should().Be("known-id");
        ids["Unidentified"].Should().Be("tag-unidentified");
        ids["New"].Should().Be("tag-new");
        ids["Duo A"].Should().BeNull();
        ids["Duo B"].Should().BeNull();
    }

    private static readonly DateTime FirstScan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SongFileMetadata Tags(string title, string artist, string? artistSortName = null) => new()
    {
        FilePath = $@"C:\Music\{title}.mp3",
        Title = title,
        Album = $"{artist} Album",
        Artists = [artist],
        ArtistSortNames = artistSortName is null ? [] : [artistSortName]
    };

    // A file read again needs a later time than its last scan.
    private async Task ScanAsync(Folder folder, params (SongFileMetadata Tags, DateTime Modified)[] files)
    {
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(files.Select(file => (file.Tags.FilePath, file.Modified)).ToList());
        foreach (var (tags, modified) in files)
        {
            tags.FileModifiedDate = modified;
            _metadataService.ExtractMetadataAsync(tags.FilePath, folder.Path).Returns(tags);
        }
        (await _library.Folders.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();
    }

    private async Task<Folder> AddFolderAsync()
    {
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        context.Folders.Add(folder);
        await context.SaveChangesAsync();
        return folder;
    }

    private async Task<List<string>> ArtistOrderAsync() =>
        (await _libraryReader.GetArtistsAsync(null, ArtistSortOrder.NameAsc, 1, 10)).Items.Select(a => a.Name).ToList();

    [Fact]
    public async Task RescanFolderForMusicAsync_SortsArtistsByTheirSortTagsAlsoWhereSongsWereNotReadAgain()
    {
        var folder = await AddFolderAsync();
        (SongFileMetadata, DateTime)[] library =
        [
            (Tags("know1", "(K)NoW_NAME"), FirstScan), (Tags("know2", "(K)NoW_NAME"), FirstScan),
            (Tags("abba", "ABBA"), FirstScan), (Tags("zed", "Zed"), FirstScan)
        ];
        await ScanAsync(folder, library);

        // Only know1 is read again; know2 and the album must follow.
        library[0] = (Tags("know1", "(K)NoW_NAME", "KNoW NAME"), FirstScan.AddMinutes(1));
        await ScanAsync(folder, library);

        (await ArtistOrderAsync()).Should().Equal("ABBA", "(K)NoW_NAME", "Zed");
        (await _libraryReader.GetAlbumsAsync(null, AlbumSortOrder.ArtistAsc, 1, 10)).Items.Select(a => a.PrimaryArtistName)
            .Should().Equal("ABBA", "(K)NoW_NAME", "Zed");
        (await _libraryReader.GetSongsAsync(SongScope.Library, null, SongSortOrder.ArtistAsc, 1, 10)).Items.Select(s => s.Title)
            .Should().Equal("abba", "know1", "know2", "zed");
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_SortsAnArtistByItsNameAgainOnceItsSortTagIsRemoved()
    {
        var folder = await AddFolderAsync();
        await ScanAsync(folder, (Tags("zed", "Zed", "Aaa"), FirstScan), (Tags("abba", "ABBA"), FirstScan));
        (await ArtistOrderAsync()).Should().Equal("Zed", "ABBA");

        await ScanAsync(folder, (Tags("zed", "Zed"), FirstScan.AddMinutes(1)), (Tags("abba", "ABBA"), FirstScan));

        (await ArtistOrderAsync()).Should().Equal("ABBA", "Zed");
        (await _libraryReader.GetSongsAsync(SongScope.Library, null, SongSortOrder.ArtistAsc, 1, 10)).Items.Select(s => s.Title)
            .Should().Equal("abba", "zed");
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_SortsAnArtistByTheFirstOfItsSongsSortTagsWhicheverIsReadLast()
    {
        var folder = await AddFolderAsync();
        (SongFileMetadata, DateTime)[] library =
        [
            (Tags("one", "Zed", "Aaa"), FirstScan), (Tags("two", "Zed", "Zzz"), FirstScan),
            (Tags("three", "Zed"), FirstScan), (Tags("mid", "Mid"), FirstScan)
        ];
        await ScanAsync(folder, library);
        (await ArtistOrderAsync()).Should().Equal("Zed", "Mid");

        library[1] = (Tags("two", "Zed", "Zzz"), FirstScan.AddMinutes(1));
        await ScanAsync(folder, library);

        (await ArtistOrderAsync()).Should().Equal("Zed", "Mid");
    }

    [Fact]
    public async Task RemovingTheOnlyTaggedSong_SortsTheArtistByItsNameAgain()
    {
        var folder = await AddFolderAsync();
        await ScanAsync(folder, (Tags("tagged", "Zed", "Aaa"), FirstScan), (Tags("plain", "Zed"), FirstScan),
            (Tags("abba", "ABBA"), FirstScan));
        (await ArtistOrderAsync()).Should().Equal("Zed", "ABBA");

        await ScanAsync(folder, (Tags("plain", "Zed"), FirstScan), (Tags("abba", "ABBA"), FirstScan));

        (await ArtistOrderAsync()).Should().Equal("ABBA", "Zed");
    }

    [Fact]
    public void Song_SyncDenormalizedFields_CorrectlyJoinsArtists()
    {

        var song = new Song { Title = "Test" };
        song.SongArtists.Add(new SongArtist { Order = 1, Artist = new Artist { Name = "Artist B" } });
        song.SongArtists.Add(new SongArtist { Order = 0, Artist = new Artist { Name = "Artist A" } });

        song.SyncDenormalizedFields();

        song.ArtistName.Should().Be("Artist A & Artist B");
        song.PrimaryArtistName.Should().Be("Artist A");
    }

    [Fact]
    public async Task AddingTrackToExistingAlbum_PreservesMetadataAndPersistsChangedAlbumArtists()
    {
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        var primary = new Artist { Name = "Primary" };
        var album = new Album { Title = "Album", Year = 1990, CoverArtUri = @"C:\cache\original.jpg" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = primary, Order = 0 });
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Albums.Add(album);
            await context.SaveChangesAsync();
        }
        var filePath = @"C:\Music\new.mp3";
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (filePath, DateTime.UtcNow) });
        _metadataService.ExtractMetadataAsync(filePath, folder.Path).Returns(new SongFileMetadata
        {
            FilePath = filePath,
            Title = "New track",
            Album = "Album",
            Year = 2024,
            Artists = ["Primary"],
            AlbumArtists = ["Primary", "Guest"],
            CoverArtUri = @"C:\cache\new.jpg"
        });

        (await _library.Folders.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var saved = await assertContext.Albums.Include(a => a.AlbumArtists).ThenInclude(a => a.Artist).SingleAsync();
        saved.Id.Should().Be(album.Id);
        saved.Year.Should().Be(1990);
        saved.CoverArtUri.Should().Be(album.CoverArtUri);
        saved.AlbumArtists.OrderBy(a => a.Order).Select(a => a.Artist.Name).Should().Equal("Primary", "Guest");
        (await assertContext.Songs.SingleAsync()).AlbumId.Should().Be(album.Id);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_SeparatesAlbumKeysContainingDelimiters()
    {
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }
        var first = new SongFileMetadata { FilePath = @"C:\Music\one.mp3", Title = "One", Album = "A|B", Artists = ["C"] };
        var second = new SongFileMetadata { FilePath = @"C:\Music\two.mp3", Title = "Two", Album = "A", Artists = ["B|C"] };
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (first.FilePath, DateTime.UtcNow), (second.FilePath, DateTime.UtcNow) });
        _metadataService.ExtractMetadataAsync(first.FilePath, folder.Path).Returns(first);
        _metadataService.ExtractMetadataAsync(second.FilePath, folder.Path).Returns(second);

        (await _library.Folders.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var songs = await assertContext.Songs.Include(s => s.Album).OrderBy(s => s.Title).ToListAsync();
        songs.Select(s => s.Album!.Title).Should().Equal("A|B", "A");
        songs.Select(s => s.AlbumId).Should().OnlyHaveUniqueItems();
    }
}
