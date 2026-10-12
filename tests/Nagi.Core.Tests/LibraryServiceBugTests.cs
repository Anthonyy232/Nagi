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
using Nagi.Core.Services.Data;

namespace Nagi.Core.Tests;

public class LibraryServiceBugTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IFileSystemService _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILastFmMetadataService _lastFmService;
    private readonly LibraryService _libraryService;
    private readonly LibraryReader _libraryReader;
    private readonly ILogger<LibraryService> _logger;
    private readonly IMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly ISettingsService _settingsService;
    private readonly IReplayGainService _replayGainService;
    private readonly IMusicBrainzService _musicBrainzService;
    private readonly IFanartTvService _fanartTvService;
    private readonly ITheAudioDbService _theAudioDbService;
    private readonly IImageProcessor _imageProcessor;
    private readonly ProviderPipelineProvider _pipelines;

    public LibraryServiceBugTests()
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _metadataService = Substitute.For<IMetadataService>();
        _lastFmService = Substitute.For<ILastFmMetadataService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _settingsService = Substitute.For<ISettingsService>();
        _replayGainService = Substitute.For<IReplayGainService>();
        _musicBrainzService = Substitute.For<IMusicBrainzService>();
        _fanartTvService = Substitute.For<IFanartTvService>();
        _theAudioDbService = Substitute.For<ITheAudioDbService>();
        _imageProcessor = Substitute.For<IImageProcessor>();
        _logger = Substitute.For<ILogger<LibraryService>>();

        _dbHelper = new DbContextFactoryTestHelper(useFileDatabase: true);
        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ImageDownload);

        _libraryReader = new LibraryReader(_dbHelper.ContextFactory, _fileSystem, _settingsService, Substitute.For<ILogger<LibraryReader>>());

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _libraryReader,
            _fileSystem,
            _metadataService,
            _lastFmService,
            _musicBrainzService,
            _fanartTvService,
            _theAudioDbService,
            _httpClientFactory,
            _pathConfig,
            _settingsService,
            _replayGainService,
            _imageProcessor,
            _pipelines,
            _logger);
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _libraryReader.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WhenDatabaseWriteFails_DoesNotDeadlock()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\BrokenScan", Name = "BrokenScan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectSongInsert BEFORE INSERT ON Songs
                BEGIN SELECT RAISE(ABORT, 'Simulated database write failure'); END;
                """);
        }

        var songFiles = Enumerable.Range(1, 200)
            .Select(i => ($"C:\\Music\\BrokenScan\\song{i}.mp3", DateTime.UtcNow))
            .ToList();
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(songFiles);
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");
        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), folder.Path)
            .Returns(call => new SongFileMetadata
            {
                FilePath = call.ArgAt<string>(0),
                Title = "Song",
                Artists = ["Artist"]
            });

        var result = await _libraryService.RescanFolderForMusicAsync(folder.Id)
            .WaitAsync(TimeSpan.FromSeconds(5));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_DuringSlowExtraction_AllowsListenHistoryWrites()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\ConcurrentScan", Name = "ConcurrentScan" };
        var existingSong = new Song
        {
            Id = Guid.NewGuid(),
            FolderId = folder.Id,
            FilePath = "C:\\Music\\ConcurrentScan\\existing.mp3",
            Title = "Existing"
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(existingSong);
            await context.SaveChangesAsync();
        }

        var slowFile = "C:\\Music\\ConcurrentScan\\slow.mp3";
        var files = Enumerable.Range(1, 100)
            .Select(i => ($"C:\\Music\\ConcurrentScan\\song{i}.mp3", DateTime.UtcNow))
            .Append((slowFile, DateTime.UtcNow))
            .Append((existingSong.FilePath, DateTime.UtcNow))
            .ToList();
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories).Returns(files);
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");

        var releaseSlowExtraction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), folder.Path)
            .Returns(async call =>
            {
                var path = call.ArgAt<string>(0);
                if (path == slowFile)
                    await releaseSlowExtraction.Task;
                return new SongFileMetadata { FilePath = path, Title = Path.GetFileNameWithoutExtension(path) };
            });

        var scanTask = _libraryService.RescanFolderForMusicAsync(folder.Id);
        try
        {
            await WaitForSongCountAsync(51);
            var sessionId = await _libraryService.StartListenSessionAsync(
                existingSong.Id,
                new PlaybackContext(PlaybackContextType.Library, null));

            sessionId.Should().NotBeNull();
        }
        finally
        {
            releaseSlowExtraction.TrySetResult();
            (await scanTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Songs.CountAsync()).Should().Be(102);
    }

    private async Task WaitForSongCountAsync(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = _dbHelper.ContextFactory.CreateDbContext();
            if (await context.Songs.CountAsync() >= expected)
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException($"Expected at least {expected} songs.");
    }

    [Fact]
    public async Task RemoveFolderAsync_WhenBackgroundRefreshIsScanning_CancelsScanAndRemovesFolder()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\BusyScan", Name = "BusyScan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        var filePath = "C:\\Music\\BusyScan\\song.mp3";
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (filePath, DateTime.UtcNow) });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");

        var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowExtractionToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataService.ExtractMetadataAsync(filePath, Arg.Any<string?>())
            .Returns(async _ =>
            {
                extractionStarted.TrySetResult();
                await allowExtractionToFinish.Task;
                return new SongFileMetadata
                {
                    FilePath = filePath,
                    Title = "Song",
                    Artists = new List<string> { "Artist" }
                };
            });

        var refreshTask = _libraryService.RefreshAllFoldersAsync();
        await extractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var removeTask = _libraryService.RemoveFolderAsync(folder.Id);
        var removed = await removeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var refreshResult = await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        allowExtractionToFinish.TrySetResult();

        removed.Should().BeTrue();
        refreshResult.Should().BeFalse("the in-flight refresh was cancelled by folder removal");

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(0);
        (await assertContext.Songs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RemoveFolderAsync_WhenManualRescanIsQueued_CancelsRunningAndQueuedScans()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\QueuedScan", Name = "QueuedScan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        var filePath = "C:\\Music\\QueuedScan\\song.mp3";
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (filePath, DateTime.UtcNow) });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");

        var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowExtractionToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractionCount = 0;
        _metadataService.ExtractMetadataAsync(filePath, Arg.Any<string?>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref extractionCount);
                extractionStarted.TrySetResult();
                await allowExtractionToFinish.Task;
                return new SongFileMetadata
                {
                    FilePath = filePath,
                    Title = "Song",
                    Artists = new List<string> { "Artist" }
                };
            });

        var refreshTask = _libraryService.RefreshAllFoldersAsync();
        await extractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queuedRescanTask = _libraryService.RescanFolderForMusicAsync(folder.Id);
        var removeTask = _libraryService.RemoveFolderAsync(folder.Id);
        allowExtractionToFinish.TrySetResult();

        var removed = await removeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var refreshResult = await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedRescanResult = await queuedRescanTask.WaitAsync(TimeSpan.FromSeconds(5));

        removed.Should().BeTrue();
        refreshResult.Should().BeFalse();
        queuedRescanResult.Should().BeFalse();
        extractionCount.Should().Be(1, "the queued rescan should be cancelled before it starts extracting metadata");
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithChangedArtists_UpdatesRelationshipsAndCleansUpOrphans()
    {
        var folderId = Guid.NewGuid();
        var folder = new Folder { Id = folderId, Path = "C:\\Music\\Scan", Name = "Scan" };

        var mainArtist = new Artist { Name = "Main Artist" };
        var legacyArtist = new Artist { Name = "Legacy Artist" };

        var album = new Album { Title = "Test Album" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = mainArtist, Order = 0 });

        var song = new Song
        {
            Title = "Test Song",
            FilePath = "C:\\Music\\Scan\\song.mp3",
            DirectoryPath = "C:\\Music\\Scan",
            FolderId = folderId,
            Album = album,
            FileModifiedDate = DateTime.UtcNow.AddDays(-1)
        };
        song.SongArtists.Add(new SongArtist { Artist = mainArtist, Order = 0 });
        song.SongArtists.Add(new SongArtist { Artist = legacyArtist, Order = 1 });

        album.SyncDenormalizedFields();
        song.SyncDenormalizedFields();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.AddRange(mainArtist, legacyArtist);
            context.Albums.Add(album);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { ("C:\\Music\\Scan\\song.mp3", DateTime.UtcNow) });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");

        var collaboratorName = "Collaborator";
        _metadataService.ExtractMetadataAsync("C:\\Music\\Scan\\song.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
            {
                FilePath = "C:\\Music\\Scan\\song.mp3",
                Title = "Test Song",
                Album = "Test Album",
                Artists = new List<string> { "Main Artist", collaboratorName },
                AlbumArtists = new List<string> { "Main Artist", collaboratorName },
                FileModifiedDate = DateTime.UtcNow
            });

        var result = await _libraryService.RescanFolderForMusicAsync(folderId);

        result.Should().BeTrue();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var dbAlbum = await context.Albums
                .Include(a => a.AlbumArtists)
                .ThenInclude(aa => aa.Artist)
                .FirstOrDefaultAsync(a => a.Title == "Test Album");

            dbAlbum.Should().NotBeNull();
            dbAlbum!.Id.Should().Be(album.Id);
            dbAlbum.AlbumArtists.Should().HaveCount(2);
            dbAlbum.AlbumArtists.OrderBy(aa => aa.Order).Select(aa => aa.Artist.Name)
                .Should().ContainInOrder("Main Artist", "Collaborator");
            dbAlbum.ArtistName.Should().Be("Main Artist & Collaborator");

            var dbSong = await context.Songs
                .Include(s => s.SongArtists)
                .ThenInclude(sa => sa.Artist)
                .FirstOrDefaultAsync(s => s.FilePath == "C:\\Music\\Scan\\song.mp3");

            dbSong.Should().NotBeNull();
            dbSong.SongArtists.Should().HaveCount(2);
            dbSong.SongArtists.OrderBy(sa => sa.Order).Select(sa => sa.Artist.Name)
                .Should().ContainInOrder("Main Artist", "Collaborator");
            dbSong.ArtistName.Should().Be("Main Artist & Collaborator");

            var legacyArtistFromDb = await context.Artists.FirstOrDefaultAsync(a => a.Name == "Legacy Artist");
            legacyArtistFromDb.Should().BeNull("Legacy Artist should be deleted because it is no longer referenced");

            var collaboratorFromDb = await context.Artists.FirstOrDefaultAsync(a => a.Name == "Collaborator");
            collaboratorFromDb.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task ForceRescanMetadataAsync_PreservesMediaAssetsWhileUpdatingGenres()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Genres", Name = "Genres" };
        var artist = new Artist { Name = "Artist" };
        var existingGenre = new Genre { Name = "Electronic" };
        var oldGenre = new Genre { Name = "Electronic\\Techno" };
        var filePath = "C:\\Music\\Genres\\song.mp3";
        var modified = DateTime.UtcNow.AddDays(-1);
        var song = new Song
        {
            Title = "Song",
            FilePath = filePath,
            DirectoryPath = folder.Path,
            FolderId = folder.Id,
            FileModifiedDate = modified,
            AlbumArtUriFromTrack = "cached-cover.jpg",
            LightSwatchId = "light",
            DarkSwatchId = "dark",
            LrcFilePath = "cached-lyrics.lrc"
        };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        song.Genres.Add(existingGenre);
        song.Genres.Add(oldGenre);
        song.SyncDenormalizedFields();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (filePath, modified) });
        _fileSystem.GetExtension(filePath).Returns(".mp3");
        _metadataService.ExtractMetadataAsync(filePath, folder.Path, false)
            .Returns(new SongFileMetadata
            {
                FilePath = filePath,
                FileModifiedDate = modified,
                Title = "Song",
                Artists = ["Artist"],
                AlbumArtists = ["Artist"],
                Genres = ["Electronic", "Techno"]
            });

        var result = await _libraryService.ForceRescanMetadataAsync().WaitAsync(TimeSpan.FromSeconds(5));

        result.Should().BeTrue();
        await _metadataService.Received(1).ExtractMetadataAsync(filePath, folder.Path, false);
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updated = await assertContext.Songs.Include(s => s.Genres).SingleAsync();
        updated.Genres.Select(g => g.Name).Should().BeEquivalentTo("Electronic", "Techno");
        updated.AlbumArtUriFromTrack.Should().Be("cached-cover.jpg");
        updated.LightSwatchId.Should().Be("light");
        updated.DarkSwatchId.Should().Be("dark");
        updated.LrcFilePath.Should().Be("cached-lyrics.lrc");
    }

    [Fact]
    public async Task ForceRescanMetadataAsync_WhenFolderIsUnavailable_ReportsFailure()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "Z:\\Offline", Name = "Offline" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(folder.Path).Returns(false);

        var act = () => _libraryService.ForceRescanMetadataAsync();

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithUntrimmedArtistNames_DoesNotThrow()
    {
        var folderId = Guid.NewGuid();
        var folder = new Folder { Id = folderId, Path = "C:\\Music\\TrimTest", Name = "TrimTest" };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { ("C:\\Music\\TrimTest\\song1.mp3", DateTime.UtcNow), ("C:\\Music\\TrimTest\\song2.mp3", DateTime.UtcNow) });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");

        _metadataService.ExtractMetadataAsync("C:\\Music\\TrimTest\\song1.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
            {
                FilePath = "C:\\Music\\TrimTest\\song1.mp3",
                Title = "Song 1",
                Artists = new List<string> { " Artist One " },
                AlbumArtists = new List<string> { " Artist One " }
            });

        _metadataService.ExtractMetadataAsync("C:\\Music\\TrimTest\\song2.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
            {
                FilePath = "C:\\Music\\TrimTest\\song2.mp3",
                Title = "Song 2",
                Artists = new List<string> { "Artist One" },
                AlbumArtists = new List<string> { "Artist One" }
            });

        var result = await _libraryService.RescanFolderForMusicAsync(folderId);

        result.Should().BeTrue();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var artists = await context.Artists.ToListAsync();
            artists.Should().HaveCount(1);
            artists[0].Name.Should().Be("Artist One");

            var songs = await context.Songs
                .Include(s => s.SongArtists)
                .ThenInclude(sa => sa.Artist)
                .ToListAsync();

            songs.Should().HaveCount(2);
            songs[0].SongArtists.First().Artist.Name.Should().Be("Artist One");
            songs[1].SongArtists.First().Artist.Name.Should().Be("Artist One");
        }
    }
}
