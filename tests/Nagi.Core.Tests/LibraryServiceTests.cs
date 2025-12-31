using System.Net;
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
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Provides comprehensive unit tests for the <see cref="LibraryService" />.
///     These tests leverage an in-memory SQLite database via <see cref="DbContextFactoryTestHelper" />
///     to ensure realistic database interactions while maintaining test isolation. All external
///     dependencies are mocked to focus testing on the service's business logic.
/// </summary>
public class LibraryServiceTests : IDisposable
{
    /// <summary>
    ///     A helper that provides a clean, in-memory SQLite database context for each test.
    /// </summary>
    private readonly DbContextFactoryTestHelper _dbHelper;

    // Mocks for external dependencies, enabling isolated testing of the service's logic.
    private readonly IFileSystemService _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILastFmMetadataService _lastFmService;

    /// <summary>
    ///     The instance of the service under test.
    /// </summary>
    private readonly LibraryService _libraryService;

    private readonly ILogger<LibraryService> _logger;

    private readonly IMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISpotifyService _spotifyService;
    private readonly ISettingsService _settingsService;
    private readonly IReplayGainService _replayGainService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LibraryServiceTests" /> class.
    ///     This constructor sets up the required mocks, the in-memory database, and instantiates
    ///     the <see cref="LibraryService" /> with these test dependencies.
    /// </summary>
    public LibraryServiceTests()
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _metadataService = Substitute.For<IMetadataService>();
        _lastFmService = Substitute.For<ILastFmMetadataService>();
        _spotifyService = Substitute.For<ISpotifyService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _settingsService = Substitute.For<ISettingsService>();
        _replayGainService = Substitute.For<IReplayGainService>();
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<LibraryService>>();

        _dbHelper = new DbContextFactoryTestHelper();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _pathConfig.AlbumArtCachePath.Returns("C:\\cache\\albumart");
        _pathConfig.ArtistImageCachePath.Returns("C:\\cache\\artistimages");
        _pathConfig.LrcCachePath.Returns("C:\\cache\\lrc");

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _fileSystem,
            _metadataService,
            _lastFmService,
            _spotifyService,
            _httpClientFactory,
            _serviceScopeFactory,
            _pathConfig,
            _settingsService,
            _replayGainService,
            _logger);
    }

    /// <summary>
    ///     Performs application-defined tasks associated with freeing, releasing, or resetting
    ///     unmanaged resources. This method ensures test isolation by disposing the in-memory
    ///     database and other disposable resources after each test execution.
    /// </summary>
    public void Dispose()
    {
        _libraryService.Dispose();
        _dbHelper.Dispose();
        _httpMessageHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Constructor Tests

    /// <summary>
    ///     Verifies that the <see cref="LibraryService" /> constructor throws an
    ///     <see cref="ArgumentNullException" /> when any of its required dependencies are null.
    ///     This test ensures the service cannot be instantiated in an invalid state.
    /// </summary>
    [Fact]
    public void Constructor_WithNullDependency_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new LibraryService(null!, _fileSystem, _metadataService,
            _lastFmService, _spotifyService, _httpClientFactory, _serviceScopeFactory, _pathConfig, _settingsService,
            _replayGainService, _logger));
        Assert.Throws<ArgumentNullException>(() => new LibraryService(_dbHelper.ContextFactory, null!, _metadataService,
            _lastFmService, _spotifyService, _httpClientFactory, _serviceScopeFactory, _pathConfig, _settingsService,
            _replayGainService, _logger));
        Assert.Throws<ArgumentNullException>(() => new LibraryService(_dbHelper.ContextFactory, _fileSystem, null!,
            _lastFmService, _spotifyService, _httpClientFactory, _serviceScopeFactory, _pathConfig, _settingsService,
            _replayGainService, _logger));
        Assert.Throws<ArgumentNullException>(() => new LibraryService(_dbHelper.ContextFactory, _fileSystem,
            _metadataService, null!, _spotifyService, _httpClientFactory, _serviceScopeFactory, _pathConfig,
            _settingsService, _replayGainService, _logger));
        Assert.Throws<ArgumentNullException>(() => new LibraryService(_dbHelper.ContextFactory, _fileSystem,
            _metadataService, _lastFmService, null!, _httpClientFactory, _serviceScopeFactory, _pathConfig,
            _settingsService, _replayGainService, _logger));
        Assert.Throws<ArgumentNullException>(() => new LibraryService(_dbHelper.ContextFactory, _fileSystem,
            _metadataService, _lastFmService, _spotifyService, null!, _serviceScopeFactory, _pathConfig,
            _settingsService, _replayGainService, _logger));
        Assert.Throws<ArgumentNullException>(() => new LibraryService(_dbHelper.ContextFactory, _fileSystem,
            _metadataService, _lastFmService, _spotifyService, _httpClientFactory, null!, _pathConfig, _settingsService,
            _replayGainService, _logger));
        Assert.Throws<ArgumentNullException>(() => new LibraryService(_dbHelper.ContextFactory, _fileSystem,
            _metadataService, _lastFmService, _spotifyService, _httpClientFactory, _serviceScopeFactory, null!,
            _settingsService, _replayGainService, _logger));
        Assert.Throws<ArgumentNullException>(() => new LibraryService(_dbHelper.ContextFactory, _fileSystem,
            _metadataService, _lastFmService, _spotifyService, _httpClientFactory, _serviceScopeFactory, _pathConfig,
            null!, _replayGainService, _logger));
        Assert.Throws<ArgumentNullException>(() => new LibraryService(_dbHelper.ContextFactory, _fileSystem,
            _metadataService, _lastFmService, _spotifyService, _httpClientFactory, _serviceScopeFactory, _pathConfig,
            _settingsService, null!, _logger));
        Assert.Throws<ArgumentNullException>(() => new LibraryService(_dbHelper.ContextFactory, _fileSystem,
            _metadataService, _lastFmService, _spotifyService, _httpClientFactory, _serviceScopeFactory, _pathConfig,
            _settingsService, _replayGainService, null!));
    }

    #endregion

    #region Data Reset Tests

    /// <summary>
    ///     Verifies that <see cref="LibraryService.ClearAllLibraryDataAsync" /> performs a complete
    ///     reset of all library data. This includes truncating all relevant database tables and
    ///     deleting and recreating the file system cache directories.
    /// </summary>
    [Fact]
    public async Task ClearAllLibraryDataAsync_WhenCalled_DeletesAllDataAndRecreatesDirectories()
    {
        // Arrange: Seed the database with data to ensure the method has content to delete.
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(new Folder { Name = "Test", Path = "C:\\Music" });
            context.Artists.Add(new Artist { Name = "Test Artist" });
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(true);

        // Act: Execute the data clearing method.
        await _libraryService.ClearAllLibraryDataAsync();

        // Assert: Verify that the database tables are empty.
        await using (var assertContext = _dbHelper.ContextFactory.CreateDbContext())
        {
            (await assertContext.Folders.CountAsync()).Should().Be(0);
            (await assertContext.Artists.CountAsync()).Should().Be(0);
            (await assertContext.Songs.CountAsync()).Should().Be(0);
        }

        // Assert: Verify that the cache directories were deleted and recreated.
        _fileSystem.Received(1).DeleteDirectory(_pathConfig.AlbumArtCachePath, true);
        _fileSystem.Received(1).DeleteDirectory(_pathConfig.ArtistImageCachePath, true);
        _fileSystem.Received(1).DeleteDirectory(_pathConfig.LrcCachePath, true);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.AlbumArtCachePath);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.ArtistImageCachePath);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.LrcCachePath);
    }

    #endregion

    #region Folder Management Tests

    /// <summary>
    ///     Verifies that <see cref="LibraryService.AddFolderAsync" /> successfully adds a new folder
    ///     to the database when provided with a valid, previously unknown path.
    /// </summary>
    [Fact]
    public async Task AddFolderAsync_WithValidNewPath_AddsFolderToDatabase()
    {
        // Arrange
        const string folderPath = "C:\\Music\\Rock";
        var lastWriteTime = DateTime.UtcNow;
        _fileSystem.GetLastWriteTimeUtc(folderPath).Returns(lastWriteTime);
        _fileSystem.GetFileNameWithoutExtension(folderPath).Returns("Rock");

        // Act
        var result = await _libraryService.AddFolderAsync(folderPath);

        // Assert: The returned folder object should be correctly populated.
        result.Should().NotBeNull();
        result!.Path.Should().Be(folderPath);
        result.Name.Should().Be("Rock");
        result.LastModifiedDate.Should().Be(lastWriteTime);

        // Assert: The database should now contain exactly one folder.
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(1);
    }

    /// <summary>
    ///     Verifies that calling <see cref="LibraryService.AddFolderAsync" /> with a path that
    ///     already exists in the database returns the existing folder entity without creating a duplicate.
    /// </summary>
    [Fact]
    public async Task AddFolderAsync_WithExistingPath_ReturnsExistingFolderWithoutAdding()
    {
        // Arrange: Pre-populate the database with a folder.
        const string folderPath = "C:\\Music\\Pop";
        var existingFolder = new Folder { Path = folderPath, Name = "Pop" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(existingFolder);
            await context.SaveChangesAsync();
        }

        // Act: Attempt to add the same folder again.
        var result = await _libraryService.AddFolderAsync(folderPath);

        // Assert: The result should be the original folder object.
        result.Should().NotBeNull();
        result!.Id.Should().Be(existingFolder.Id);

        // Assert: The database count should remain unchanged.
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(1);
    }

    /// <summary>
    ///     Verifies that <see cref="LibraryService.RemoveFolderAsync" /> correctly removes a folder,
    ///     its associated songs, cleans up any orphaned artists and albums that are no longer referenced,
    ///     and deletes related cached files from the file system.
    /// </summary>
    [Fact]
    public async Task RemoveFolderAsync_WithExistingFolder_RemovesFolderAndAssociatedData()
    {
        // Arrange: Create a complete data graph (Folder -> Song -> Album -> Artist) to test cascading cleanup.
        var folder = new Folder { Path = "C:\\Music\\Jazz", Name = "Jazz" };
        var artist = new Artist { Name = "Jazz Artist" };
        var album = new Album { Title = "Jazz Album", Artist = artist };
        var song = new Song
        {
            FilePath = "C:\\Music\\Jazz\\track1.mp3",
            Title = "Jazz Track",
            Folder = folder,
            Artist = artist,
            Album = album,
            AlbumArtUriFromTrack = "C:\\cache\\albumart\\art1.jpg",
            LrcFilePath = "C:\\cache\\lrc\\lyrics1.lrc"
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.FileExists("C:\\cache\\albumart\\art1.jpg").Returns(true);
        _fileSystem.FileExists("C:\\cache\\lrc\\lyrics1.lrc").Returns(true);
        _pathConfig.LrcCachePath.Returns("C:\\cache\\lrc");

        // Act
        var result = await _libraryService.RemoveFolderAsync(folder.Id);

        // Assert: The operation should report success.
        result.Should().BeTrue();

        // Assert: All related entities should be removed from the database.
        await using (var assertContext = _dbHelper.ContextFactory.CreateDbContext())
        {
            (await assertContext.Folders.CountAsync()).Should().Be(0);
            (await assertContext.Songs.CountAsync()).Should().Be(0);
            (await assertContext.Albums.CountAsync()).Should().Be(0);
            (await assertContext.Artists.CountAsync()).Should().Be(0);
        }

        // Assert: The service should have attempted to delete the associated cache files.
        _fileSystem.Received(1).DeleteFile("C:\\cache\\albumart\\art1.jpg");
        _fileSystem.Received(1).DeleteFile("C:\\cache\\lrc\\lyrics1.lrc");
    }

    #endregion

    #region Library Scanning Tests

    /// <summary>
    ///     Verifies that <see cref="LibraryService.RescanFolderForMusicAsync" /> correctly synchronizes
    ///     the library state with the file system. This comprehensive test covers a mixed scenario
    ///     involving newly added, modified, and deleted files within a single scan operation.
    /// </summary>
    [Fact]
    public async Task RescanFolderForMusicAsync_WithMixedChanges_CorrectlyUpdatesLibrary()
    {
        // Arrange: Set up the initial database state with three songs.
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Scan", Name = "Scan" };
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist" };
        var existingSongUnchanged = new Song
        {
            FilePath = "C:\\Music\\Scan\\unchanged.mp3", FileModifiedDate = new DateTime(2023, 1, 1),
            FolderId = folder.Id, ArtistId = artist.Id
        };
        var existingSongToUpdate = new Song
        {
            FilePath = "C:\\Music\\Scan\\updated.mp3", FileModifiedDate = new DateTime(2023, 1, 1),
            FolderId = folder.Id, ArtistId = artist.Id
        };
        var existingSongToDelete = new Song
        {
            FilePath = "C:\\Music\\Scan\\deleted.mp3", FileModifiedDate = new DateTime(2023, 1, 1),
            FolderId = folder.Id, ArtistId = artist.Id
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(existingSongUnchanged, existingSongToUpdate, existingSongToDelete);
            await context.SaveChangesAsync();
        }

        // Arrange: Mock the new state of the file system.
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories).Returns(new[]
        {
            "C:\\Music\\Scan\\unchanged.mp3", // Still exists, same timestamp
            "C:\\Music\\Scan\\updated.mp3", // Still exists, new timestamp
            "C:\\Music\\Scan\\new.mp3" // New file
        });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");
        _fileSystem.GetLastWriteTimeUtc("C:\\Music\\Scan\\unchanged.mp3").Returns(new DateTime(2023, 1, 1));
        _fileSystem.GetLastWriteTimeUtc("C:\\Music\\Scan\\updated.mp3").Returns(new DateTime(2023, 2, 2)); // Modified
        _fileSystem.GetLastWriteTimeUtc("C:\\Music\\Scan\\new.mp3").Returns(new DateTime(2023, 3, 3));

        // Arrange: Mock metadata extraction for the new and updated files.
        _metadataService.ExtractMetadataAsync("C:\\Music\\Scan\\updated.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
                { FilePath = "C:\\Music\\Scan\\updated.mp3", Title = "Updated Song", Artist = "Artist" });
        _metadataService.ExtractMetadataAsync("C:\\Music\\Scan\\new.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
                { FilePath = "C:\\Music\\Scan\\new.mp3", Title = "New Song", Artist = "Artist" });

        // Act
        var result = await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert: The operation should succeed.
        result.Should().BeTrue();

        // Assert: The database state should accurately reflect all file system changes.
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var songs = await assertContext.Songs.ToListAsync();
        songs.Should().HaveCount(3);
        songs.Should().Contain(s => s.FilePath == "C:\\Music\\Scan\\unchanged.mp3");
        songs.Should().Contain(s => s.FilePath == "C:\\Music\\Scan\\updated.mp3" && s.Title == "Updated Song");
        songs.Should().Contain(s => s.FilePath == "C:\\Music\\Scan\\new.mp3" && s.Title == "New Song");
        songs.Should().NotContain(s => s.FilePath == "C:\\Music\\Scan\\deleted.mp3");
    }

    /// <summary>
    ///     Verifies that if a folder's path no longer exists on the file system,
    ///     <see cref="LibraryService.RescanFolderForMusicAsync" /> correctly removes the folder
    ///     and all its associated contents from the library database.
    /// </summary>
    [Fact]
    public async Task RescanFolderForMusicAsync_WhenFolderPathNoLongerExists_RemovesFolderFromLibrary()
    {
        // Arrange: Add a folder to the database that will be simulated as deleted from disk.
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\DeletedFolder", Name = "Deleted" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(false);

        // Act
        var result = await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert: The operation should succeed and the folder should be removed from the database.
        result.Should().BeTrue();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(0);
    }

    /// <summary>
    ///     Verifies that a scan operation can be gracefully cancelled via a <see cref="CancellationToken" />.
    ///     The method should stop processing, return false, and not throw a cancellation exception.
    /// </summary>
    [Fact]
    public async Task RescanFolderForMusicAsync_WhenCancelled_StopsProcessingAndReturnsFalse()
    {
        // Arrange
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Scan", Name = "Scan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFiles(folder.Path, Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { "C:\\Music\\Scan\\file1.mp3" });
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel before the operation starts.

        // Act
        var result = await _libraryService.RescanFolderForMusicAsync(folder.Id, cancellationToken: cts.Token);

        // Assert: The operation should report failure and should not have proceeded to metadata extraction.
        result.Should().BeFalse();
        await _metadataService.DidNotReceive().ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    #endregion

    #region Artist Metadata Fetching Tests

    /// <summary>
    ///     Verifies that <see cref="LibraryService.GetArtistDetailsAsync" /> fetches remote metadata
    ///     for an artist when `allowOnlineFetch` is true. It should update the artist's biography,
    ///     download and cache their image, update the database record, and raise the
    ///     `ArtistMetadataUpdated` event.
    /// </summary>
    [Fact]
    public async Task GetArtistDetailsAsync_WithMissingMetadataAndFetchAllowed_FetchesAndUpdatesArtist()
    {
        // Arrange: Create an artist with no biography or image path.
        var artist = new Artist
            { Id = Guid.NewGuid(), Name = "Remote Artist", Biography = null, LocalImageCachePath = null };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }

        // Arrange: Mock successful responses from remote metadata services.
        _lastFmService.GetArtistInfoAsync(artist.Name)
            .Returns(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo { Biography = "A cool bio." }));
        _spotifyService.GetArtistImageUrlAsync(artist.Name)
            .Returns(ServiceResult<SpotifyImageResult>.FromSuccess(new SpotifyImageResult
                { ImageUrl = "http://example.com/image.jpg" }));

        // Arrange: Configure file system mocks for image caching.
        var artistCachePath = _pathConfig.ArtistImageCachePath;
        var artistImageFilename = $"{artist.Id}.fetched.jpg";
        var expectedImagePath = Path.Combine(artistCachePath, artistImageFilename);
        _fileSystem.Combine(artistCachePath, artistImageFilename).Returns(expectedImagePath);
        _fileSystem.FileExists(expectedImagePath).Returns(false);
        _fileSystem.WriteAllBytesAsync(expectedImagePath, Arg.Any<byte[]>()).Returns(Task.CompletedTask);

        // Arrange: Mock a successful HTTP response for the image download.
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        });

        // Arrange: Set up an event handler to verify the ArtistMetadataUpdated event is raised correctly.
        var eventFired = false;
        _libraryService.ArtistMetadataUpdated += (sender, args) =>
        {
            eventFired = true;
            args.ArtistId.Should().Be(artist.Id);
            args.NewLocalImageCachePath.Should().Be(expectedImagePath);
        };

        // Act
        var result = await _libraryService.GetArtistDetailsAsync(artist.Id, true);

        // Assert: The returned artist object and database record should be updated.
        result.Should().NotBeNull();
        result!.Biography.Should().Be("A cool bio.");
        result.LocalImageCachePath.Should().Be(expectedImagePath);
        result.MetadataLastCheckedUtc.Should().NotBeNull();
        eventFired.Should().BeTrue();

        // Assert: The service should have written the downloaded image to the cache.
        await _fileSystem.Received(1).WriteAllBytesAsync(expectedImagePath, Arg.Any<byte[]>());
    }

    /// <summary>
    ///     Verifies that <see cref="LibraryService.GetArtistDetailsAsync" /> does not attempt to
    ///     fetch remote data from external services if the `allowOnlineFetch` parameter is false.
    /// </summary>
    [Fact]
    public async Task GetArtistDetailsAsync_WithFetchDisallowed_DoesNotCallRemoteServices()
    {
        // Arrange
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Local Artist", Biography = null };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _libraryService.GetArtistDetailsAsync(artist.Id, false);

        // Assert: The artist data should remain unchanged.
        result.Should().NotBeNull();
        result!.Biography.Should().BeNull();

        // Assert: No network calls should have been made.
        await _lastFmService.DidNotReceive().GetArtistInfoAsync(Arg.Any<string>());
        await _spotifyService.DidNotReceive().GetArtistImageUrlAsync(Arg.Any<string>());
    }

    #endregion

    #region Playlist Management Tests

    /// <summary>
    ///     Verifies that <see cref="LibraryService.CreatePlaylistAsync" /> successfully creates
    ///     and persists a new playlist with the specified name.
    /// </summary>
    [Fact]
    public async Task CreatePlaylistAsync_WithValidName_CreatesPlaylist()
    {
        // Act
        var playlist = await _libraryService.CreatePlaylistAsync("My Awesome Mix");

        // Assert
        playlist.Should().NotBeNull();
        playlist!.Name.Should().Be("My Awesome Mix");
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Playlists.CountAsync()).Should().Be(1);
    }

    /// <summary>
    ///     Verifies that <see cref="LibraryService.AddSongsToPlaylistAsync" /> correctly adds new songs
    ///     to a playlist while ignoring songs that are already present. It also ensures that the
    ///     order of songs is correctly maintained.
    /// </summary>
    [Fact]
    public async Task AddSongsToPlaylistAsync_WithNewAndExistingSongs_AddsOnlyNewSongsInOrder()
    {
        // Arrange: Create dependent entities and a playlist with one existing song.
        var artist = new Artist { Name = "Artist" };
        var folder = new Folder { Name = "Folder", Path = "C:\\" };
        var song1 = new Song { Title = "Song 1", Artist = artist, Folder = folder, FilePath = "C:\\song1.mp3" };
        var song2 = new Song { Title = "Song 2", Artist = artist, Folder = folder, FilePath = "C:\\song2.mp3" };
        var song3 = new Song { Title = "Song 3", Artist = artist, Folder = folder, FilePath = "C:\\song3.mp3" };
        var playlist = new Playlist { Name = "Test Playlist" };
        playlist.PlaylistSongs.Add(new PlaylistSong { Song = song1, Order = 0 });
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Playlists.Add(playlist);
            context.Songs.AddRange(song2, song3);
            await context.SaveChangesAsync();
        }

        // Act: Attempt to add three songs, one of which is a duplicate.
        var result = await _libraryService.AddSongsToPlaylistAsync(playlist.Id, new[] { song2.Id, song1.Id, song3.Id });

        // Assert: The operation should succeed and the playlist should contain all three unique songs in the correct order.
        result.Should().BeTrue();
        var songsInPlaylist = await _libraryService.GetSongsInPlaylistOrderedAsync(playlist.Id);
        songsInPlaylist.Should().HaveCount(3);
        songsInPlaylist.Select(s => s.Title).Should().ContainInOrder("Song 1", "Song 2", "Song 3");
    }

    /// <summary>
    ///     Verifies that <see cref="LibraryService.RemoveSongsFromPlaylistAsync" /> correctly removes
    ///     the specified songs and re-indexes the `Order` of the remaining songs to maintain a
    ///     contiguous, zero-based sequence.
    /// </summary>
    [Fact]
    public async Task RemoveSongsFromPlaylistAsync_WhenCalled_RemovesSongsAndReindexesOrder()
    {
        // Arrange: Create a playlist with three songs.
        var artist = new Artist { Name = "Artist" };
        var folder = new Folder { Name = "Folder", Path = "C:\\" };
        var song1 = new Song { Title = "Song 1", Artist = artist, Folder = folder, FilePath = "C:\\song1.mp3" };
        var song2 = new Song { Title = "Song 2", Artist = artist, Folder = folder, FilePath = "C:\\song2.mp3" };
        var song3 = new Song { Title = "Song 3", Artist = artist, Folder = folder, FilePath = "C:\\song3.mp3" };
        var playlist = new Playlist { Name = "Test Playlist" };
        playlist.PlaylistSongs.Add(new PlaylistSong { Song = song1, Order = 0 });
        playlist.PlaylistSongs.Add(new PlaylistSong { Song = song2, Order = 1 });
        playlist.PlaylistSongs.Add(new PlaylistSong { Song = song3, Order = 2 });
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Playlists.Add(playlist);
            await context.SaveChangesAsync();
        }

        // Act: Remove the middle song from the playlist.
        await _libraryService.RemoveSongsFromPlaylistAsync(playlist.Id, new[] { song2.Id });

        // Assert: The playlist should now contain two songs in the correct, re-indexed order.
        var songsInPlaylist = await _libraryService.GetSongsInPlaylistOrderedAsync(playlist.Id);
        songsInPlaylist.Should().HaveCount(2);
        songsInPlaylist.Select(s => s.Title).Should().ContainInOrder("Song 1", "Song 3");
    }

    #endregion

    #region Paged Loading Tests

    /// <summary>
    ///     Verifies that paged loading methods, such as <see cref="LibraryService.GetAllSongsPagedAsync" />,
    ///     return the correct subset of data and accurate pagination metadata for a given page number and size.
    /// </summary>
    [Fact]
    public async Task GetAllSongsPagedAsync_RequestsSecondPage_ReturnsCorrectSubsetOfSongs()
    {
        // Arrange: Create 25 songs to test pagination across multiple pages.
        var artist = new Artist { Name = "Artist" };
        var folder = new Folder { Name = "Folder", Path = "C:\\" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            context.Folders.Add(folder);
            for (var i = 1; i <= 25; i++)
                context.Songs.Add(new Song
                {
                    Title = $"Song {i:D2}", ArtistId = artist.Id, FolderId = folder.Id, FilePath = $"C:\\song{i:D2}.mp3"
                });
            await context.SaveChangesAsync();
        }

        // Act: Request the second page of 10 items.
        var result = await _libraryService.GetAllSongsPagedAsync(2, 10);

        // Assert: The paged result object should have the correct metadata and items.
        result.Should().NotBeNull();
        result.TotalCount.Should().Be(25);
        result.PageNumber.Should().Be(2);
        result.PageSize.Should().Be(10);
        result.TotalPages.Should().Be(3);
        result.Items.Should().HaveCount(10);
        result.Items.First().Title.Should().Be("Song 11");
        result.Items.Last().Title.Should().Be("Song 20");
    }

    /// <summary>
    ///     Verifies that paged loading methods sanitize invalid input parameters (e.g., zero or negative
    ///     page number/size) to valid minimums (1) to prevent errors.
    /// </summary>
    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(-5, -5)]
    public async Task PagedMethods_WithInvalidPageParameters_SanitizesToMinimumOfOne(int pageNumber, int pageSize)
    {
        // Arrange: Create a single song.
        var artist = new Artist { Name = "Artist" };
        var folder = new Folder { Name = "Folder", Path = "C:\\" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(new Song { Title = "A", Artist = artist, Folder = folder, FilePath = "C:\\song.mp3" });
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _libraryService.GetAllSongsPagedAsync(pageNumber, pageSize);

        // Assert: The returned page number and size should be at least 1.
        result.PageNumber.Should().Be(1);
        result.PageSize.Should().Be(Math.Max(1, pageSize));
    }

    #endregion
}