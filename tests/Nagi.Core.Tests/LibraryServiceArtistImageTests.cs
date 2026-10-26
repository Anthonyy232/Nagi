using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;
public class LibraryServiceArtistImageTests : IDisposable
{
    private const string ArtistImageCachePath = "C:\\cache\\artistimages";
    private const string FolderPath = "C:\\Music\\Artist";
    private const string AlbumDir = "C:\\Music\\Artist\\Album";
    private const string SongPath = "C:\\Music\\Artist\\Album\\song.mp3";
    private const string ArtistImagePath = "C:\\Music\\Artist\\artist.jpg";
    private const string AlbumArtistImagePath = "C:\\Music\\Artist\\Album\\artist.jpg";

    private static readonly byte[] ImageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
    private static readonly byte[] ProcessedBytes = new byte[] { 0x10, 0x20, 0x30 };

    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IFileSystemService _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IImageProcessor _imageProcessor;
    private readonly LibraryService _libraryService;
    private readonly LibraryReader _libraryReader;
    private readonly ILogger<LibraryService> _logger;
    private readonly IMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly ISettingsService _settingsService;
    private readonly IReplayGainService _replayGainService;
    private readonly ProviderPipelineProvider _pipelines;

    public LibraryServiceArtistImageTests()
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _metadataService = Substitute.For<IMetadataService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _settingsService = Substitute.For<ISettingsService>();
        _replayGainService = Substitute.For<IReplayGainService>();
        _imageProcessor = Substitute.For<IImageProcessor>();
        _logger = Substitute.For<ILogger<LibraryService>>();

        _dbHelper = new DbContextFactoryTestHelper();

        _pathConfig.ArtistImageCachePath.Returns(ArtistImageCachePath);
        _pathConfig.AlbumArtCachePath.Returns("C:\\cache\\albumart");
        _pathConfig.PlaylistImageCachePath.Returns("C:\\cache\\playlistimages");
        _pathConfig.LrcCachePath.Returns("C:\\cache\\lrc");

        _fileSystem.GetFiles(Arg.Any<string>(), Arg.Any<string>()).Returns(Array.Empty<string>());
        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(false);
        _fileSystem.FileExists(Arg.Any<string>()).Returns(false);
        _fileSystem.WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>()).Returns(Task.CompletedTask);
        _fileSystem.ReadAllBytesAsync(Arg.Any<string>()).Returns(Task.FromResult(Array.Empty<byte>()));

        _fileSystem.DirectoryExists(ArtistImageCachePath).Returns(true);

        _imageProcessor.ProcessImageBytesAsync(ImageBytes).Returns(Task.FromResult(ProcessedBytes));

        _settingsService.GetEnabledServiceProvidersAsync(Arg.Any<ServiceCategory>())
            .Returns(Task.FromResult(new List<ServiceProviderSetting>()));

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ImageDownload);

        _libraryReader = new LibraryReader(_dbHelper.ContextFactory, _fileSystem, _settingsService, Substitute.For<ILogger<LibraryReader>>());

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _libraryReader,
            _fileSystem,
            _metadataService,
            Substitute.For<IArtistMetadataService>(),
            Substitute.For<IAlbumCoverService>(),
            _httpClientFactory,
            _pathConfig,
            _settingsService,
            _replayGainService,
            _imageProcessor,
            _pipelines,
            _logger);
    }

    [Theory]
    [InlineData(true, true, ".local")]
    [InlineData(false, true, ".fetched")]
    [InlineData(false, false, null)]
    public async Task RemoveArtistImage_RestoresHighestPriorityFallback(bool hasLocal, bool hasFetched, string? suffix)
    {
        var (_, artist, _) = await SeedUpToDateSongAsync();
        var customPath = Path.Combine(ArtistImageCachePath, $"{artist.Id}.custom.jpg");
        var localPath = Path.Combine(ArtistImageCachePath, $"{artist.Id}.local.jpg");
        var fetchedPath = Path.Combine(ArtistImageCachePath, $"{artist.Id}.fetched.jpg");
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var stored = await context.Artists.FindAsync(artist.Id);
            stored!.LocalImageCachePath = customPath;
            await context.SaveChangesAsync();
        }
        _fileSystem.FileExists(customPath).Returns(true);
        _fileSystem.FileExists(localPath).Returns(hasLocal);
        _fileSystem.FileExists(fetchedPath).Returns(hasFetched);
        string? notifiedPath = customPath;
        _libraryService.ArtistMetadataUpdated += (_, args) => notifiedPath = args.NewLocalImageCachePath;

        (await _libraryService.RemoveArtistImageAsync(artist.Id)).Should().BeTrue();

        var expected = suffix is null ? null : Path.Combine(ArtistImageCachePath, $"{artist.Id}{suffix}.jpg");
        (await GetArtistLocalImagePathAsync(artist.Id)).Should().Be(expected);
        notifiedPath.Should().Be(expected);
        _fileSystem.Received(1).DeleteFile(customPath);
        _fileSystem.DidNotReceive().DeleteFile(localPath);
        _fileSystem.DidNotReceive().DeleteFile(fetchedPath);
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _libraryReader.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
    }

    // Matching timestamps exercise image discovery without importing song metadata again.
    private async Task<(Folder folder, Artist artist, Song song)> SeedUpToDateSongAsync(
        string? localImageCachePath = null)
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var folder = new Folder { Id = Guid.NewGuid(), Name = "Artist", Path = FolderPath };
        var artist = new Artist { Name = "Test Artist", LocalImageCachePath = localImageCachePath, MetadataLastCheckedUtc = null };
        var song = new Song
        {
            Title = "Song",
            FilePath = SongPath,
            DirectoryPath = AlbumDir,
            FolderId = folder.Id,
            FileModifiedDate = timestamp
        };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        song.SyncDenormalizedFields();

        await using var ctx = _dbHelper.ContextFactory.CreateDbContext();
        ctx.Folders.Add(folder);
        ctx.Artists.Add(artist);
        ctx.Songs.Add(song);
        await ctx.SaveChangesAsync();

        _fileSystem.DirectoryExists(FolderPath).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(FolderPath, "*.*", SearchOption.AllDirectories).Returns(new[] { (SongPath, timestamp) });

        return (folder, artist, song);
    }

    private async Task<string?> GetArtistLocalImagePathAsync(Guid artistId)
    {
        await using var ctx = _dbHelper.ContextFactory.CreateDbContext();
        return await ctx.Artists.AsNoTracking()
            .Where(a => a.Id == artistId)
            .Select(a => a.LocalImageCachePath)
            .FirstOrDefaultAsync();
    }

    private void SetupArtistImageAt(string imagePath)
    {
        var dir = Path.GetDirectoryName(imagePath)!;
        _fileSystem.GetFiles(dir, "*.*").Returns(new[] { imagePath });
        _fileSystem.ReadAllBytesAsync(imagePath).Returns(Task.FromResult(ImageBytes));
    }

    [Theory]
    [InlineData(ArtistImagePath)]
    [InlineData(AlbumArtistImagePath)]
    public async Task RescanFolder_ArtistImageInRelatedDirectory_SavesAndNotifies(string imagePath)
    {
        var (folder, artist, _) = await SeedUpToDateSongAsync();
        SetupArtistImageAt(imagePath);
        ArtistMetadataUpdatedEventArgs? notification = null;
        _libraryService.ArtistMetadataUpdated += (_, args) => notification = args;

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().Be(Path.Combine(ArtistImageCachePath, $"{artist.Id}.local.jpg"));
        notification.Should().NotBeNull();
        notification!.ArtistId.Should().Be(artist.Id);
        notification.NewLocalImageCachePath.Should().Be(localPath);
    }

    [Theory]
    [InlineData(".custom")]
    [InlineData(".local")]
    public async Task RescanFolder_ArtistWithPriorityImage_SkipsLocalFolderScan(string suffix)
    {
        var customPath = $"C:\\cache\\artistimages\\abc{suffix}.jpg";
        var (folder, artist, _) = await SeedUpToDateSongAsync(localImageCachePath: customPath);
        _fileSystem.FileExists(customPath).Returns(true);

        SetupArtistImageAt(ArtistImagePath);

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().Be(customPath);
        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(Arg.Any<byte[]>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RescanFolder_ArtistWhoseCachedImageIsMissing_LooksForItAgain(bool hasFolderImage)
    {
        var deletedPath = "C:\\cache\\artistimages\\abc.custom.jpg";
        var (folder, artist, _) = await SeedUpToDateSongAsync(localImageCachePath: deletedPath);
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            await context.Artists.ExecuteUpdateAsync(setters =>
                setters.SetProperty(a => a.MetadataLastCheckedUtc, DateTime.UtcNow));
        }
        if (hasFolderImage) SetupArtistImageAt(ArtistImagePath);
        var notifiedPaths = new List<string?>();
        _libraryService.ArtistMetadataUpdated += (_, args) => notifiedPaths.Add(args.NewLocalImageCachePath);

        // Reporting a change makes the scan start the online lookup afterwards.
        (await _libraryService.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

        var expectedPath = hasFolderImage ? Path.Combine(ArtistImageCachePath, $"{artist.Id}.local.jpg") : null;
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updated = await assertContext.Artists.SingleAsync();
        updated.LocalImageCachePath.Should().Be(expectedPath);
        updated.MetadataLastCheckedUtc.Should().BeNull(because: "the online lookup may find an image again");
        notifiedPaths.Should().EndWith(expectedPath);
    }

    [Fact]
    public async Task RescanFolder_AlbumOnlyArtistWhoseCachedImageIsMissing_IsCleared()
    {
        var (folder, _, song) = await SeedUpToDateSongAsync();
        var albumArtist = new Artist
        {
            Name = "Various Artists",
            LocalImageCachePath = "C:\\cache\\artistimages\\va.fetched.jpg",
            MetadataLastCheckedUtc = DateTime.UtcNow
        };
        var album = new Album { Title = "Compilation" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = albumArtist, Order = 0 });
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Albums.Add(album);
            await context.SaveChangesAsync();
            await context.Songs.Where(s => s.Id == song.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.AlbumId, album.Id));
        }

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updated = await assertContext.Artists.SingleAsync(a => a.Id == albumArtist.Id);
        updated.LocalImageCachePath.Should().BeNull();
        updated.MetadataLastCheckedUtc.Should().BeNull();
    }

    [Fact]
    public async Task RescanFolder_NoArtistImageFile_DoesNotSetLocalImagePath()
    {
        var (folder, artist, _) = await SeedUpToDateSongAsync();
        var eventFired = false;
        _libraryService.ArtistMetadataUpdated += (_, _) => eventFired = true;

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().BeNull(because: "no image file was found on disk");
        eventFired.Should().BeFalse();
    }

    [Fact]
    public async Task RescanFolder_ArtistJpgInBothPassA_AndPassB_PassATakesPriority()
    {
        var (folder, artist, _) = await SeedUpToDateSongAsync();

        var parentImagePath = ArtistImagePath;
        var albumImagePath = AlbumArtistImagePath;

        var parentBytes = new byte[] { 0x01, 0x02 };
        var albumBytes = new byte[] { 0x03, 0x04 };

        _fileSystem.GetFiles(FolderPath, "*.*").Returns(new[] { parentImagePath });
        _fileSystem.ReadAllBytesAsync(parentImagePath).Returns(Task.FromResult(parentBytes));

        _fileSystem.GetFiles(AlbumDir, "*.*").Returns(new[] { albumImagePath });
        _fileSystem.ReadAllBytesAsync(albumImagePath).Returns(Task.FromResult(albumBytes));

        _imageProcessor.ProcessImageBytesAsync(parentBytes).Returns(Task.FromResult(ProcessedBytes));

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        await _imageProcessor.Received(1).ProcessImageBytesAsync(parentBytes);
        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(albumBytes);
    }

    [Fact]
    public async Task RescanFolder_ImageProcessingThrows_LogsWarningAndContinues()
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Artist", Path = FolderPath };
        var artist1 = new Artist { Name = "Artist One" };
        var artist2 = new Artist { Name = "Artist Two" };

        const string album2Dir = "C:\\Music\\Artist\\Album2";
        const string song2Path = "C:\\Music\\Artist\\Album2\\song2.mp3";
        const string album2ImagePath = "C:\\Music\\Artist\\Album2\\artist.jpg";

        var song1 = new Song { Title = "S1", FilePath = SongPath, DirectoryPath = AlbumDir, FolderId = folder.Id, FileModifiedDate = timestamp };
        song1.SongArtists.Add(new SongArtist { Artist = artist1, Order = 0 });
        song1.SyncDenormalizedFields();

        var song2 = new Song { Title = "S2", FilePath = song2Path, DirectoryPath = album2Dir, FolderId = folder.Id, FileModifiedDate = timestamp };
        song2.SongArtists.Add(new SongArtist { Artist = artist2, Order = 0 });
        song2.SyncDenormalizedFields();

        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Artists.AddRange(artist1, artist2);
            ctx.Songs.AddRange(song1, song2);
            await ctx.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(FolderPath).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(FolderPath, "*.*", SearchOption.AllDirectories).Returns(new[] { (SongPath, timestamp), (song2Path, timestamp) });

        var artist1Bytes = new byte[] { 0x01, 0x02 };
        var artist2Bytes = new byte[] { 0x03, 0x04 };

        _fileSystem.GetFiles(AlbumDir, "*.*").Returns(new[] { AlbumArtistImagePath });
        _fileSystem.GetFiles(album2Dir, "*.*").Returns(new[] { album2ImagePath });
        _fileSystem.ReadAllBytesAsync(AlbumArtistImagePath).Returns(Task.FromResult(artist1Bytes));
        _fileSystem.ReadAllBytesAsync(album2ImagePath).Returns(Task.FromResult(artist2Bytes));

        _imageProcessor.ProcessImageBytesAsync(artist1Bytes)
            .ThrowsAsync(new InvalidOperationException("corrupt image"));
        _imageProcessor.ProcessImageBytesAsync(artist2Bytes)
            .Returns(Task.FromResult(ProcessedBytes));

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        var path1 = await GetArtistLocalImagePathAsync(artist1.Id);
        var path2 = await GetArtistLocalImagePathAsync(artist2.Id);
        path1.Should().BeNull(because: "image processing threw for artist1");
        path2.Should().NotBeNullOrEmpty(because: "processing succeeded for artist2 even after artist1 failed");
    }

    [Fact]
    public async Task RescanFolder_ArtistImageBytesAreEmpty_DoesNotSetLocalImagePath()
    {
        var (folder, artist, _) = await SeedUpToDateSongAsync();

        _fileSystem.GetFiles(FolderPath, "*.*").Returns(new[] { ArtistImagePath });
        _fileSystem.ReadAllBytesAsync(ArtistImagePath).Returns(Task.FromResult(Array.Empty<byte>()));

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().BeNull(because: "empty bytes must not produce a cached image");
        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(Arg.Any<byte[]>());
    }

    [Fact]
    public async Task RescanFolder_ArtistWithOtherImagePath_IsRescannedAndReplaced()
    {
        const string legacyPath = "C:\\cache\\artistimages\\abc.png";
        var (folder, artist, _) = await SeedUpToDateSongAsync(localImageCachePath: legacyPath);
        SetupArtistImageAt(ArtistImagePath);

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().NotBe(legacyPath, because: "a local folder image should replace a non-local/non-custom path");
        localPath.Should().Contain(".local.");
    }

    [Fact]
    public async Task RescanFolder_CancellationRequested_ReturnsFalseAndDoesNotSave()
    {
        var (folder, artist, _) = await SeedUpToDateSongAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _libraryService.RescanFolderForMusicAsync(folder.Id, cancellationToken: cts.Token);

        result.Should().BeFalse(because: "a cancelled scan returns false without propagating the exception");
        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(Arg.Any<byte[]>());
        var path1 = await GetArtistLocalImagePathAsync(artist.Id);
        path1.Should().BeNull(because: "cancellation must prevent any DB write");
    }

    [Fact]
    public async Task GetArtistDetails_LocalArtistJpgFound_SetsLocalImagePathBeforeOnlineProviders()
    {
        var (_, artist, _) = await SeedUpToDateSongAsync();
        SetupArtistImageAt(ArtistImagePath);

        var expectedPath = Path.Combine(ArtistImageCachePath, $"{artist.Id}.local.jpg");

        var result = await _libraryService.GetArtistDetailsAsync(artist.Id, allowOnlineFetch: true);

        result.Should().NotBeNull();
        result!.LocalImageCachePath.Should().Be(expectedPath,
            because: "the local folder image must be stored before contacting online providers");

        await _fileSystem.Received(1).WriteAllBytesAsync(Arg.Any<string>(), ProcessedBytes);
        _fileSystem.Received(1).MoveFile(Arg.Any<string>(), expectedPath, true);
    }

    [Fact]
    public async Task GetArtistDetails_ArtistWithExistingLocalImage_SkipsFolderScan()
    {
        const string existingLocal = "C:\\cache\\artistimages\\existing.local.jpg";
        var (_, artist, _) = await SeedUpToDateSongAsync(localImageCachePath: existingLocal);

        _fileSystem.GetFiles(FolderPath, "*.*").Returns(new[] { ArtistImagePath });

        await _libraryService.GetArtistDetailsAsync(artist.Id, allowOnlineFetch: true);

        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(Arg.Any<byte[]>());
    }

    [Fact]
    public async Task FindArtistImageInDirectory_OnlyMatchesArtistFileName()
    {
        var (folder, artist, _) = await SeedUpToDateSongAsync();

        var otherJpgPath = Path.Combine(FolderPath, "other.jpg");
        var artistJpgPath = Path.Combine(FolderPath, "artist.jpg");
        var artistBytes = new byte[] { 0x01, 0x02 };

        _fileSystem.GetFiles(FolderPath, "*.*").Returns(new[] { otherJpgPath, artistJpgPath });

        _fileSystem.ReadAllBytesAsync(artistJpgPath).Returns(Task.FromResult(artistBytes));
        _imageProcessor.ProcessImageBytesAsync(artistBytes).Returns(Task.FromResult(ProcessedBytes));

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        await _imageProcessor.Received(1).ProcessImageBytesAsync(artistBytes);
    }
}
