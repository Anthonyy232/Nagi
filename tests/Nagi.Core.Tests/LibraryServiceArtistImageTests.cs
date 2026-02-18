using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Tests for the Navidrome-style local artist image scanning feature.
///     Covers <c>FindArtistImageInFoldersAsync</c> (via <see cref="LibraryService.GetArtistDetailsAsync"/>)
///     and <c>UpdateMissingArtistImagesFromFoldersAsync</c> (via <see cref="LibraryService.RescanFolderForMusicAsync"/>).
/// </summary>
public class LibraryServiceArtistImageTests : IDisposable
{
    // ── paths used across tests ────────────────────────────────────────────────
    private const string ArtistImageCachePath = "C:\\cache\\artistimages";
    private const string FolderPath           = "C:\\Music\\Artist";
    private const string AlbumDir             = "C:\\Music\\Artist\\Album";
    private const string SongPath             = "C:\\Music\\Artist\\Album\\song.mp3";
    private const string ArtistImagePath      = "C:\\Music\\Artist\\artist.jpg";
    private const string AlbumArtistImagePath = "C:\\Music\\Artist\\Album\\artist.jpg";

    private static readonly byte[] ImageBytes     = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
    private static readonly byte[] ProcessedBytes = new byte[] { 0x10, 0x20, 0x30 };

    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IFileSystemService         _fileSystem;
    private readonly IHttpClientFactory         _httpClientFactory;
    private readonly IImageProcessor            _imageProcessor;
    private readonly ILastFmMetadataService     _lastFmService;
    private readonly LibraryService             _libraryService;
    private readonly ILogger<LibraryService>    _logger;
    private readonly IMetadataService           _metadataService;
    private readonly IPathConfiguration         _pathConfig;
    private readonly IServiceScopeFactory       _serviceScopeFactory;
    private readonly ISpotifyService            _spotifyService;
    private readonly ISettingsService           _settingsService;
    private readonly IReplayGainService         _replayGainService;
    private readonly IMusicBrainzService        _musicBrainzService;
    private readonly IFanartTvService           _fanartTvService;
    private readonly ITheAudioDbService         _theAudioDbService;
    private readonly IApiKeyService             _apiKeyService;

    public LibraryServiceArtistImageTests()
    {
        _fileSystem          = Substitute.For<IFileSystemService>();
        _metadataService     = Substitute.For<IMetadataService>();
        _lastFmService       = Substitute.For<ILastFmMetadataService>();
        _spotifyService      = Substitute.For<ISpotifyService>();
        _httpClientFactory   = Substitute.For<IHttpClientFactory>();
        _serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        _pathConfig          = Substitute.For<IPathConfiguration>();
        _settingsService     = Substitute.For<ISettingsService>();
        _replayGainService   = Substitute.For<IReplayGainService>();
        _musicBrainzService  = Substitute.For<IMusicBrainzService>();
        _fanartTvService     = Substitute.For<IFanartTvService>();
        _theAudioDbService   = Substitute.For<ITheAudioDbService>();
        _apiKeyService       = Substitute.For<IApiKeyService>();
        _imageProcessor      = Substitute.For<IImageProcessor>();
        _logger              = Substitute.For<ILogger<LibraryService>>();

        _dbHelper = new DbContextFactoryTestHelper();

        // ── path configuration ──────────────────────────────────────────────
        _pathConfig.ArtistImageCachePath.Returns(ArtistImageCachePath);
        _pathConfig.AlbumArtCachePath.Returns("C:\\cache\\albumart");
        _pathConfig.PlaylistImageCachePath.Returns("C:\\cache\\playlistimages");
        _pathConfig.LrcCachePath.Returns("C:\\cache\\lrc");

        // ── file system helpers — delegate to real Path.* so paths are consistent ──
        _fileSystem.Combine(Arg.Any<string[]>())
            .Returns(ci => Path.Combine(ci.ArgAt<string[]>(0)));
        _fileSystem.GetDirectoryName(Arg.Any<string>())
            .Returns(ci => Path.GetDirectoryName(ci.ArgAt<string>(0)));
        _fileSystem.GetExtension(Arg.Any<string>())
            .Returns(ci => Path.GetExtension(ci.ArgAt<string>(0)) ?? string.Empty);
        _fileSystem.GetFileNameWithoutExtension(Arg.Any<string>())
            .Returns(ci => Path.GetFileNameWithoutExtension(ci.ArgAt<string>(0)) ?? string.Empty);

        // Default: no files, all directories missing, no bytes
        _fileSystem.GetFiles(Arg.Any<string>(), Arg.Any<string>()).Returns(Array.Empty<string>());
        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(false);
        _fileSystem.FileExists(Arg.Any<string>()).Returns(false);
        _fileSystem.WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>()).Returns(Task.CompletedTask);
        _fileSystem.ReadAllBytesAsync(Arg.Any<string>()).Returns(Task.FromResult(Array.Empty<byte>()));

        // Artist image cache directory always exists (pre-created)
        _fileSystem.DirectoryExists(ArtistImageCachePath).Returns(true);

        // Image processor: return processed bytes for our fixture
        _imageProcessor.ProcessImageBytesAsync(ImageBytes).Returns(Task.FromResult(ProcessedBytes));

        // No online providers by default so the method exits cleanly without HTTP
        _settingsService.GetEnabledServiceProvidersAsync(Arg.Any<ServiceCategory>())
            .Returns(Task.FromResult(new List<ServiceProviderSetting>()));

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _fileSystem,
            _metadataService,
            _lastFmService,
            _spotifyService,
            _musicBrainzService,
            _fanartTvService,
            _theAudioDbService,
            _httpClientFactory,
            _serviceScopeFactory,
            _pathConfig,
            _settingsService,
            _replayGainService,
            _apiKeyService,
            _imageProcessor,
            _logger);
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _dbHelper.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Seeds a folder + artist + song linked by SongArtist. The song is always
    ///     up-to-date (no timestamp mismatch) so RescanFolderForMusicAsync goes to the
    ///     "nothing to process" path and calls <c>UpdateMissingArtistImagesFromFoldersAsync</c>.
    /// </summary>
    private async Task<(Folder folder, Artist artist, Song song)> SeedUpToDateSongAsync(
        string? localImageCachePath = null)
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var folder = new Folder { Id = Guid.NewGuid(), Name = "Artist", Path = FolderPath };
        var artist = new Artist { Name = "Test Artist", LocalImageCachePath = localImageCachePath, MetadataLastCheckedUtc = null };
        var song = new Song
        {
            Title        = "Song",
            FilePath     = SongPath,
            DirectoryPath = AlbumDir,
            FolderId     = folder.Id,
            FileModifiedDate = timestamp
        };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        song.SyncDenormalizedFields();

        await using var ctx = _dbHelper.ContextFactory.CreateDbContext();
        ctx.Folders.Add(folder);
        ctx.Artists.Add(artist);
        ctx.Songs.Add(song);
        await ctx.SaveChangesAsync();

        // Scan sees the file but with the same timestamp → no update needed
        _fileSystem.DirectoryExists(FolderPath).Returns(true);
        _fileSystem.EnumerateFiles(FolderPath, "*.*", SearchOption.AllDirectories).Returns(new[] { SongPath });
        _fileSystem.GetLastWriteTimeUtc(SongPath).Returns(timestamp);

        return (folder, artist, song);
    }

    /// <summary>
    ///     After a scan, returns the artist's current <see cref="Artist.LocalImageCachePath" /> from the DB.
    /// </summary>
    private async Task<string?> GetArtistLocalImagePathAsync(Guid artistId)
    {
        await using var ctx = _dbHelper.ContextFactory.CreateDbContext();
        return await ctx.Artists.AsNoTracking()
            .Where(a => a.Id == artistId)
            .Select(a => a.LocalImageCachePath)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    ///     Configures the mock file system so that an artist image is found at <paramref name="imagePath"/>
    ///     and the image processor produces <see cref="ProcessedBytes"/>. Also configures
    ///     <see cref="ArtistImageCachePath"/> FileExists so ImageStorageHelper.FindImage returns a path.
    /// </summary>
    private void SetupArtistImageAt(string imagePath, Artist artist)
    {
        var dir = Path.GetDirectoryName(imagePath)!;
        _fileSystem.GetFiles(dir, "*.*").Returns(new[] { imagePath });
        _fileSystem.ReadAllBytesAsync(imagePath).Returns(Task.FromResult(ImageBytes));

        // ImageStorageHelper.FindImage: the first extension it checks is .jpg
        var expectedCachedPath = Path.Combine(ArtistImageCachePath, $"{artist.Id}.local.jpg");
        _fileSystem.FileExists(expectedCachedPath).Returns(true);
    }

    // ── tests: UpdateMissingArtistImagesFromFoldersAsync via RescanFolderForMusicAsync ──

    /// <summary>
    ///     When an <c>artist.jpg</c> exists in the parent of the album folder (pass A —
    ///     "artist folder"), the artist's <see cref="Artist.LocalImageCachePath"/> is populated.
    /// </summary>
    [Fact]
    public async Task RescanFolder_ArtistJpgInParentDirectory_SetsLocalImagePath()
    {
        // Arrange
        var (folder, artist, _) = await SeedUpToDateSongAsync();
        SetupArtistImageAt(ArtistImagePath, artist);  // parent dir = FolderPath

        // Act
        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert
        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().NotBeNullOrEmpty(because: "artist.jpg in the artist folder should be picked up");
        localPath.Should().Contain(".local.", because: "the suffix distinguishes folder-found images from fetched ones");
        localPath.Should().EndWith(".jpg");
    }

    /// <summary>
    ///     When no <c>artist.*</c> file exists in the parent folder but one exists in the
    ///     album folder itself (pass B — "album/artist"), the image is still found.
    /// </summary>
    [Fact]
    public async Task RescanFolder_ArtistJpgInAlbumDirectory_SetsLocalImagePath()
    {
        // Arrange
        var (folder, artist, _) = await SeedUpToDateSongAsync();
        SetupArtistImageAt(AlbumArtistImagePath, artist);  // album dir = AlbumDir

        // Act
        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert
        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().NotBeNullOrEmpty(because: "artist.jpg in the album folder is pass-B coverage");
        localPath.Should().Contain(".local.");
    }

    /// <summary>
    ///     An artist whose <see cref="Artist.LocalImageCachePath"/> already contains <c>.custom.</c>
    ///     must not be scanned — custom images are user overrides that must not be replaced.
    /// </summary>
    [Fact]
    public async Task RescanFolder_ArtistWithCustomImage_SkipsLocalFolderScan()
    {
        // Arrange: artist already has a custom image
        const string customPath = "C:\\cache\\artistimages\\abc.custom.jpg";
        var (folder, artist, _) = await SeedUpToDateSongAsync(localImageCachePath: customPath);

        // Even if artist.jpg exists on disk, it must not overwrite the custom image
        SetupArtistImageAt(ArtistImagePath, artist);

        // Act
        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert: image path is still the original custom one
        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().Be(customPath, because: "custom images must never be overwritten by folder scanning");
        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(Arg.Any<byte[]>());
    }

    /// <summary>
    ///     An artist whose <see cref="Artist.LocalImageCachePath"/> already contains <c>.local.</c>
    ///     is skipped — no redundant re-processing when the image is already cached from a previous scan.
    /// </summary>
    [Fact]
    public async Task RescanFolder_ArtistWithExistingLocalImage_SkipsRedundantScan()
    {
        // Arrange: artist already has a local-folder image
        const string localPath = "C:\\cache\\artistimages\\abc.local.jpg";
        var (folder, artist, _) = await SeedUpToDateSongAsync(localImageCachePath: localPath);

        // artist.jpg is still on disk
        SetupArtistImageAt(ArtistImagePath, artist);

        // Act
        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert: path unchanged, processor not called
        var currentPath = await GetArtistLocalImagePathAsync(artist.Id);
        currentPath.Should().Be(localPath, because: "a cached .local. image should not be re-processed");
        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(Arg.Any<byte[]>());
    }

    /// <summary>
    ///     When no artist image file is found in any associated directory, the artist's
    ///     <see cref="Artist.LocalImageCachePath"/> remains null.
    /// </summary>
    [Fact]
    public async Task RescanFolder_NoArtistImageFile_DoesNotSetLocalImagePath()
    {
        // Arrange: no artist.jpg anywhere — GetFiles returns empty arrays (default mock)
        var (folder, artist, _) = await SeedUpToDateSongAsync();

        // Act
        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert
        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().BeNull(because: "no image file was found on disk");
    }

    /// <summary>
    ///     When artist.jpg exists in BOTH the parent directory (pass A) and the album directory
    ///     (pass B), the pass-A (artist-folder) image must win.
    /// </summary>
    [Fact]
    public async Task RescanFolder_ArtistJpgInBothPassA_AndPassB_PassATakesPriority()
    {
        // Arrange: artist.jpg in both FolderPath (pass A) and AlbumDir (pass B)
        var (folder, artist, _) = await SeedUpToDateSongAsync();

        var parentImagePath = ArtistImagePath;       // C:\Music\Artist\artist.jpg  (pass A)
        var albumImagePath  = AlbumArtistImagePath;  // C:\Music\Artist\Album\artist.jpg (pass B)

        var parentBytes = new byte[] { 0x01, 0x02 };
        var albumBytes  = new byte[] { 0x03, 0x04 };

        // Pass A dir (FolderPath) has an artist image
        _fileSystem.GetFiles(FolderPath, "*.*").Returns(new[] { parentImagePath });
        _fileSystem.ReadAllBytesAsync(parentImagePath).Returns(Task.FromResult(parentBytes));

        // Pass B dir (AlbumDir) also has an artist image
        _fileSystem.GetFiles(AlbumDir, "*.*").Returns(new[] { albumImagePath });
        _fileSystem.ReadAllBytesAsync(albumImagePath).Returns(Task.FromResult(albumBytes));

        // Only parentBytes should be processed
        _imageProcessor.ProcessImageBytesAsync(parentBytes).Returns(Task.FromResult(ProcessedBytes));

        var expectedCachedPath = Path.Combine(ArtistImageCachePath, $"{artist.Id}.local.jpg");
        _fileSystem.FileExists(expectedCachedPath).Returns(true);

        // Act
        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert: pass-A bytes were processed, pass-B bytes were not
        await _imageProcessor.Received(1).ProcessImageBytesAsync(parentBytes);
        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(albumBytes);
    }

    /// <summary>
    ///     The <see cref="LibraryService.ArtistMetadataUpdated"/> event must be raised for an
    ///     artist after a local image is persisted to the database.
    /// </summary>
    [Fact]
    public async Task RescanFolder_ArtistJpgFound_RaisesArtistMetadataUpdatedEvent()
    {
        var (folder, artist, _) = await SeedUpToDateSongAsync();
        SetupArtistImageAt(ArtistImagePath, artist);

        ArtistMetadataUpdatedEventArgs? capturedArgs = null;
        _libraryService.ArtistMetadataUpdated += (_, args) => capturedArgs = args;

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        capturedArgs.Should().NotBeNull(because: "event must fire when a local artist image is stored");
        capturedArgs!.ArtistId.Should().Be(artist.Id);
        capturedArgs.NewLocalImageCachePath.Should().Contain(".local.");
    }

    /// <summary>
    ///     The <see cref="LibraryService.ArtistMetadataUpdated"/> event must NOT be raised when
    ///     no artist image is found.
    /// </summary>
    [Fact]
    public async Task RescanFolder_NoArtistImageFound_DoesNotRaiseArtistMetadataUpdatedEvent()
    {
        var (folder, _, _) = await SeedUpToDateSongAsync();
        // Default mock: GetFiles returns empty

        var eventFired = false;
        _libraryService.ArtistMetadataUpdated += (_, _) => eventFired = true;

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        eventFired.Should().BeFalse(because: "no image was found, so no event should be raised");
    }

    /// <summary>
    ///     When <see cref="IImageProcessor.ProcessImageBytesAsync"/> throws for one artist,
    ///     the error is logged and processing continues for the remaining artists.
    ///     Each artist has a distinct album directory so that their image bytes are independent,
    ///     making the test deterministic regardless of DB query ordering.
    /// </summary>
    [Fact]
    public async Task RescanFolder_ImageProcessingThrows_LogsWarningAndContinues()
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var folder  = new Folder { Id = Guid.NewGuid(), Name = "Artist", Path = FolderPath };
        var artist1 = new Artist { Name = "Artist One" };
        var artist2 = new Artist { Name = "Artist Two" };

        // Artist1 is in AlbumDir; artist2 is in a separate Album2Dir so they have distinct image files.
        const string album2Dir       = "C:\\Music\\Artist\\Album2";
        const string song2Path       = "C:\\Music\\Artist\\Album2\\song2.mp3";
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
        _fileSystem.EnumerateFiles(FolderPath, "*.*", SearchOption.AllDirectories).Returns(new[] { SongPath, song2Path });
        _fileSystem.GetLastWriteTimeUtc(SongPath).Returns(timestamp);
        _fileSystem.GetLastWriteTimeUtc(song2Path).Returns(timestamp);

        // Distinct bytes per artist so we can wire processing behavior independently.
        var artist1Bytes = new byte[] { 0x01, 0x02 };
        var artist2Bytes = new byte[] { 0x03, 0x04 };

        _fileSystem.GetFiles(AlbumDir,   "*.*").Returns(new[] { AlbumArtistImagePath });
        _fileSystem.GetFiles(album2Dir,  "*.*").Returns(new[] { album2ImagePath });
        _fileSystem.ReadAllBytesAsync(AlbumArtistImagePath).Returns(Task.FromResult(artist1Bytes));
        _fileSystem.ReadAllBytesAsync(album2ImagePath).Returns(Task.FromResult(artist2Bytes));

        // Processing always throws for artist1's bytes; always succeeds for artist2's bytes.
        _imageProcessor.ProcessImageBytesAsync(artist1Bytes)
            .ThrowsAsync(new InvalidOperationException("corrupt image"));
        _imageProcessor.ProcessImageBytesAsync(artist2Bytes)
            .Returns(Task.FromResult(ProcessedBytes));

        var expectedCachedPath2 = Path.Combine(ArtistImageCachePath, $"{artist2.Id}.local.jpg");
        _fileSystem.FileExists(expectedCachedPath2).Returns(true);

        // Act — must not throw
        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert: artist1 has no image (processing failed), artist2 got one regardless of order
        var path1 = await GetArtistLocalImagePathAsync(artist1.Id);
        var path2 = await GetArtistLocalImagePathAsync(artist2.Id);
        path1.Should().BeNull(because: "image processing threw for artist1");
        path2.Should().NotBeNullOrEmpty(because: "processing succeeded for artist2 even after artist1 failed");
    }

    /// <summary>
    ///     When <see cref="IFileSystemService.ReadAllBytesAsync"/> returns an empty array,
    ///     the artist image must not be processed or stored.
    /// </summary>
    [Fact]
    public async Task RescanFolder_ArtistImageBytesAreEmpty_DoesNotSetLocalImagePath()
    {
        var (folder, artist, _) = await SeedUpToDateSongAsync();

        // artist.jpg is found but ReadAllBytesAsync returns empty
        _fileSystem.GetFiles(FolderPath, "*.*").Returns(new[] { ArtistImagePath });
        _fileSystem.ReadAllBytesAsync(ArtistImagePath).Returns(Task.FromResult(Array.Empty<byte>()));

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().BeNull(because: "empty bytes must not produce a cached image");
        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(Arg.Any<byte[]>());
    }

    /// <summary>
    ///     When <see cref="ImageStorageHelper.FindImage"/> returns null after a successful save
    ///     (e.g. unexpected caching failure), the artist's path must remain null and no event fires.
    /// </summary>
    [Fact]
    public async Task RescanFolder_FindImageReturnsNullAfterSave_DoesNotSetLocalImagePath()
    {
        var (folder, artist, _) = await SeedUpToDateSongAsync();

        _fileSystem.GetFiles(FolderPath, "*.*").Returns(new[] { ArtistImagePath });
        _fileSystem.ReadAllBytesAsync(ArtistImagePath).Returns(Task.FromResult(ImageBytes));
        _imageProcessor.ProcessImageBytesAsync(ImageBytes).Returns(Task.FromResult(ProcessedBytes));

        // FileExists returns false for every path → FindImage returns null
        _fileSystem.FileExists(Arg.Any<string>()).Returns(false);
        _fileSystem.DirectoryExists(ArtistImageCachePath).Returns(true);
        _fileSystem.DirectoryExists(FolderPath).Returns(true);
        _fileSystem.EnumerateFiles(FolderPath, "*.*", SearchOption.AllDirectories).Returns(new[] { SongPath });
        _fileSystem.GetLastWriteTimeUtc(SongPath).Returns(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var eventFired = false;
        _libraryService.ArtistMetadataUpdated += (_, _) => eventFired = true;

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().BeNull(because: "FindImage returning null must not result in a stored path");
        eventFired.Should().BeFalse(because: "no path was successfully stored");
    }

    /// <summary>
    ///     An artist with a <see cref="Artist.LocalImageCachePath"/> that contains neither
    ///     <c>.local.</c> nor <c>.custom.</c> (e.g. a legacy or external path) must be treated
    ///     as needing a scan and replaced if a local folder image is found.
    /// </summary>
    [Fact]
    public async Task RescanFolder_ArtistWithOtherImagePath_IsRescannedAndReplaced()
    {
        // Artist has a path with no known suffix — should be treated as stale/external
        const string legacyPath = "C:\\cache\\artistimages\\abc.png";
        var (folder, artist, _) = await SeedUpToDateSongAsync(localImageCachePath: legacyPath);
        SetupArtistImageAt(ArtistImagePath, artist);

        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        var localPath = await GetArtistLocalImagePathAsync(artist.Id);
        localPath.Should().NotBe(legacyPath, because: "a local folder image should replace a non-local/non-custom path");
        localPath.Should().Contain(".local.");
    }

    /// <summary>
    ///     When the <see cref="CancellationToken"/> is cancelled, <see cref="LibraryService"/>
    ///     handles it gracefully: returns <c>false</c> (does not propagate the exception) and
    ///     makes no DB writes.
    /// </summary>
    [Fact]
    public async Task RescanFolder_CancellationRequested_ReturnsFalseAndDoesNotSave()
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var folder  = new Folder { Id = Guid.NewGuid(), Name = "Artist", Path = FolderPath };
        var artist1 = new Artist { Name = "Cancelable Artist" };

        var song1 = new Song { Title = "S1", FilePath = SongPath, DirectoryPath = AlbumDir, FolderId = folder.Id, FileModifiedDate = timestamp };
        song1.SongArtists.Add(new SongArtist { Artist = artist1, Order = 0 });
        song1.SyncDenormalizedFields();

        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Artists.Add(artist1);
            ctx.Songs.Add(song1);
            await ctx.SaveChangesAsync();
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled before the scan starts

        _fileSystem.DirectoryExists(FolderPath).Returns(true);
        _fileSystem.EnumerateFiles(FolderPath, "*.*", SearchOption.AllDirectories).Returns(new[] { SongPath });
        _fileSystem.GetLastWriteTimeUtc(SongPath).Returns(timestamp);

        // Act — must NOT throw; the method catches OperationCanceledException internally
        var result = await _libraryService.RescanFolderForMusicAsync(folder.Id, cancellationToken: cts.Token);

        // Assert
        result.Should().BeFalse(because: "a cancelled scan returns false without propagating the exception");
        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(Arg.Any<byte[]>());
        var path1 = await GetArtistLocalImagePathAsync(artist1.Id);
        path1.Should().BeNull(because: "cancellation must prevent any DB write");
    }

    // ── tests: FetchAndUpdateArtistFromRemoteAsync (local image before online providers) ──

    /// <summary>
    ///     <see cref="LibraryService.GetArtistDetailsAsync"/> runs <c>FetchAndUpdateArtistFromRemoteAsync</c>
    ///     which checks local folders BEFORE contacting online providers. When an artist image is
    ///     found locally, it is stored with the <c>.local.</c> suffix and saved to the database.
    /// </summary>
    [Fact]
    public async Task GetArtistDetails_LocalArtistJpgFound_SetsLocalImagePathBeforeOnlineProviders()
    {
        // Arrange: seed a properly-linked folder + artist + song via shared helper
        var (_, artist, _) = await SeedUpToDateSongAsync();
        SetupArtistImageAt(ArtistImagePath, artist);

        var expectedPath = Path.Combine(ArtistImageCachePath, $"{artist.Id}.local.jpg");

        // Act
        var result = await _libraryService.GetArtistDetailsAsync(artist.Id, allowOnlineFetch: true);

        // Assert
        result.Should().NotBeNull();
        result!.LocalImageCachePath.Should().Be(expectedPath,
            because: "the local folder image must be stored before contacting online providers");

        // Confirm the processed bytes were written to cache
        await _fileSystem.Received(1).WriteAllBytesAsync(expectedPath, ProcessedBytes);
    }

    /// <summary>
    ///     When an artist already has a <c>.local.</c> image, <c>GetArtistDetailsAsync</c> must
    ///     not re-scan the folder or re-process any image bytes.
    /// </summary>
    [Fact]
    public async Task GetArtistDetails_ArtistWithExistingLocalImage_SkipsFolderScan()
    {
        // Arrange: artist already has a cached local image
        const string existingLocal = "C:\\cache\\artistimages\\existing.local.jpg";
        var (_, artist, _) = await SeedUpToDateSongAsync(localImageCachePath: existingLocal);

        // artist.jpg exists on disk, but must not be re-processed
        _fileSystem.GetFiles(FolderPath, "*.*").Returns(new[] { ArtistImagePath });

        // Act
        await _libraryService.GetArtistDetailsAsync(artist.Id, allowOnlineFetch: true);

        // Assert: no image processing occurred
        await _imageProcessor.DidNotReceive().ProcessImageBytesAsync(Arg.Any<byte[]>());
    }

    /// <summary>
    ///     An artist image file must be named "artist" (case-insensitive).
    /// </summary>
    [Fact]
    public async Task FindArtistImageInDirectory_OnlyMatchesArtistFileName()
    {
        // Arrange
        var (folder, artist, _) = await SeedUpToDateSongAsync();
        
        var otherJpgPath = Path.Combine(FolderPath, "other.jpg");
        var artistJpgPath = Path.Combine(FolderPath, "artist.jpg");
        var artistBytes = new byte[] { 0x01, 0x02 };

        _fileSystem.GetFiles(FolderPath, "*.*").Returns(new[] { otherJpgPath, artistJpgPath });
        _fileSystem.GetFileNameWithoutExtension(otherJpgPath).Returns("other");
        _fileSystem.GetFileNameWithoutExtension(artistJpgPath).Returns("artist");
        
        _fileSystem.ReadAllBytesAsync(artistJpgPath).Returns(Task.FromResult(artistBytes));
        _imageProcessor.ProcessImageBytesAsync(artistBytes).Returns(Task.FromResult(ProcessedBytes));

        var expectedCachedPath = Path.Combine(ArtistImageCachePath, $"{artist.Id}.local.jpg");
        _fileSystem.FileExists(expectedCachedPath).Returns(true);

        // Act
        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert: artist.jpg won, other.jpg ignored
        await _imageProcessor.Received(1).ProcessImageBytesAsync(artistBytes);
    }
}
