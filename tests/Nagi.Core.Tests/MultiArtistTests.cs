using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class MultiArtistTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IFileSystemService _fileSystem;
    private readonly IMetadataService _metadataService;
    private readonly LibraryService _libraryService;
    private readonly LibraryReader _libraryReader;
    private readonly ProviderPipelineProvider _pipelines;

    public MultiArtistTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
        _fileSystem = Substitute.For<IFileSystemService>();
        _metadataService = Substitute.For<IMetadataService>();

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var settingsService = Substitute.For<ISettingsService>();
        var pathConfig = Substitute.For<IPathConfiguration>();
        pathConfig.AlbumArtCachePath.Returns("C:\\cache\\albumart");
        pathConfig.ArtistImageCachePath.Returns("C:\\cache\\artistimages");
        pathConfig.LrcCachePath.Returns("C:\\cache\\lrc");


        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ImageDownload);

        _libraryReader = new LibraryReader(_dbHelper.ContextFactory, _fileSystem, settingsService, Substitute.For<ILogger<LibraryReader>>());

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _libraryReader,
            _fileSystem,
            _metadataService,
            Substitute.For<IArtistMetadataService>(),
            Substitute.For<IAlbumCoverService>(),
            httpClientFactory,
            pathConfig,
            settingsService,
            Substitute.For<IReplayGainService>(),
            Substitute.For<IImageProcessor>(),
            _pipelines,
            Substitute.For<ILogger<LibraryService>>()
        );
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _libraryReader.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
    }

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

        var result = await _libraryService.RescanFolderForMusicAsync(folderId);

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

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var ids = await assertContext.Artists.ToDictionaryAsync(a => a.Name, a => a.MusicBrainzId);
        ids["Known"].Should().Be("known-id");
        ids["Unidentified"].Should().Be("tag-unidentified");
        ids["New"].Should().Be("tag-new");
        ids["Duo A"].Should().BeNull();
        ids["Duo B"].Should().BeNull();
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
            FilePath = filePath, Title = "New track", Album = "Album", Year = 2024,
            Artists = ["Primary"], AlbumArtists = ["Primary", "Guest"], CoverArtUri = @"C:\cache\new.jpg"
        });

        (await _libraryService.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

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

        (await _libraryService.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var songs = await assertContext.Songs.Include(s => s.Album).OrderBy(s => s.Title).ToListAsync();
        songs.Select(s => s.Album!.Title).Should().Equal("A|B", "A");
        songs.Select(s => s.AlbumId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task PlayTransientFileAsync_PopulatesDenormalizedFields()
    {

        var filePath = "C:\\transient.mp3";
        var metadata = new SongFileMetadata
        {
            Title = "Transient Song",
            Artists = new List<string> { "Artist 1", "Artist 2" },
            Album = "Transient Album",
            AlbumArtists = new List<string> { "Artist 1" },
            Duration = TimeSpan.FromMinutes(3)
        };
        _metadataService.ExtractMetadataAsync(filePath).Returns(metadata);

        using var playbackService = new MusicPlaybackService(
            Substitute.For<ISettingsService>(),
            Substitute.For<IAudioPlayer>(),
            Substitute.For<ISystemMediaControls>(),
            Substitute.For<ILibraryReader>(),
            Substitute.For<ILibraryWriter>(),
            _metadataService,
            Substitute.For<IFileSystemService>(),
            Substitute.For<ILogger<MusicPlaybackService>>()
        );

        await playbackService.PlayTransientFileAsync(filePath);

        playbackService.CurrentTrack.Should().NotBeNull();
        playbackService.CurrentTrack!.ArtistName.Should().Be("Artist 1 & Artist 2");
        playbackService.CurrentTrack.PrimaryArtistName.Should().Be("Artist 1");

        playbackService.CurrentTrack.Album.Should().NotBeNull();
        playbackService.CurrentTrack.Album!.ArtistName.Should().Be("Artist 1");
        playbackService.CurrentTrack.Album.PrimaryArtistName.Should().Be("Artist 1");
    }
}
