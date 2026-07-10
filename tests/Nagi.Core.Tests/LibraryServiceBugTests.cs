using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
using Nagi.Core.Data;

namespace Nagi.Core.Tests;

public class LibraryServiceBugTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IFileSystemService _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILastFmMetadataService _lastFmService;
    private readonly LibraryService _libraryService;
    private readonly ILogger<LibraryService> _logger;
    private readonly IMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISettingsService _settingsService;
    private readonly IReplayGainService _replayGainService;
    private readonly IMusicBrainzService _musicBrainzService;
    private readonly IFanartTvService _fanartTvService;
    private readonly ITheAudioDbService _theAudioDbService;
    private readonly IApiKeyService _apiKeyService;
    private readonly IImageProcessor _imageProcessor;
    private readonly ProviderPipelineProvider _pipelines;

    public LibraryServiceBugTests()
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _metadataService = Substitute.For<IMetadataService>();
        _lastFmService = Substitute.For<ILastFmMetadataService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _settingsService = Substitute.For<ISettingsService>();
        _replayGainService = Substitute.For<IReplayGainService>();
        _musicBrainzService = Substitute.For<IMusicBrainzService>();
        _fanartTvService = Substitute.For<IFanartTvService>();
        _theAudioDbService = Substitute.For<ITheAudioDbService>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _imageProcessor = Substitute.For<IImageProcessor>();
        _logger = Substitute.For<ILogger<LibraryService>>();

        _dbHelper = new DbContextFactoryTestHelper();
        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ImageDownload);

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _fileSystem,
            _metadataService,
            _lastFmService,
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
            _pipelines,
            _logger);
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithMoreThan100NewSongs_DoesNotThrowArgumentException()
    {
        // Arrange
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\LargeScan", Name = "LargeScan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        var songFiles = Enumerable.Range(1, 101).Select(i => ($"C:\\Music\\LargeScan\\song{i}.mp3", DateTime.UtcNow)).ToList();
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories).Returns(songFiles);
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");

        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(x => Task.FromResult(new SongFileMetadata
            {
                FilePath = (string)x[0],
                Title = "Song",
                Artists = new List<string> { "Artist" }
            }));

        // Act & Assert
        // This should not throw System.ArgumentException: AggressiveGC requires setting the blocking parameter to true.
        Func<Task> act = async () => await _libraryService.RescanFolderForMusicAsync(folder.Id);
        await act.Should().NotThrowAsync<ArgumentException>();
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
        await Task.Delay(100);
        allowExtractionToFinish.TrySetResult();
        var removed = await removeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var refreshResult = await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));

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
        // Arrange: Setup existing folder, song, and album with multiple artists
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

        // Mock file system finding the file with a NEW timestamp
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { ("C:\\Music\\Scan\\song.mp3", DateTime.UtcNow) });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");

        // Mock metadata extraction returning NEW collaboration
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

        // Capture the exception from the logger to see what happened
        Exception? loggedException = null;
        _logger.WhenForAnyArgs(x => x.Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>()))
            .Do(call =>
            {
                var exc = call.Args()[3] as Exception;
                if (exc != null) loggedException = exc;
            });

        // Act
        var result = await _libraryService.RescanFolderForMusicAsync(folderId);

        // If failed, throw the logged exception
        if (!result && loggedException != null)
        {
            throw new InvalidOperationException($"Scan failed with exception: {loggedException.Message}", loggedException);
        }

        // Assert
        result.Should().BeTrue();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            // 1. Verify Album is updated and ID is preserved (because Primary Artist didn't change)
            var dbAlbum = await context.Albums
                .Include(a => a.AlbumArtists)
                .ThenInclude(aa => aa.Artist)
                .FirstOrDefaultAsync(a => a.Title == "Test Album");

            dbAlbum.Should().NotBeNull();
            dbAlbum.AlbumArtists.Should().HaveCount(2);
            dbAlbum.AlbumArtists.OrderBy(aa => aa.Order).Select(aa => aa.Artist.Name)
                .Should().ContainInOrder("Main Artist", "Collaborator");
            dbAlbum.ArtistName.Should().Be("Main Artist & Collaborator");

            // 2. Verify Song is updated
            var dbSong = await context.Songs
                .Include(s => s.SongArtists)
                .ThenInclude(sa => sa.Artist)
                .FirstOrDefaultAsync(s => s.FilePath == "C:\\Music\\Scan\\song.mp3");

            dbSong.Should().NotBeNull();
            dbSong.SongArtists.Should().HaveCount(2);
            dbSong.SongArtists.OrderBy(sa => sa.Order).Select(sa => sa.Artist.Name)
                .Should().ContainInOrder("Main Artist", "Collaborator");
            dbSong.ArtistName.Should().Be("Main Artist & Collaborator");

            // 3. Verify Orphans are cleaned up
            var legacyArtistFromDb = await context.Artists.FirstOrDefaultAsync(a => a.Name == "Legacy Artist");
            legacyArtistFromDb.Should().BeNull("Legacy Artist should be deleted because it is no longer referenced");

            // 4. Verify new artist exists
            var collaboratorFromDb = await context.Artists.FirstOrDefaultAsync(a => a.Name == "Collaborator");
            collaboratorFromDb.Should().NotBeNull();
        }
    }
    [Fact]
    public async Task RescanFolderForMusicAsync_WithUntrimmedArtistNames_DoesNotThrow()
    {
        // Arrange
        var folderId = Guid.NewGuid();
        var folder = new Folder { Id = folderId, Path = "C:\\Music\\TrimTest", Name = "TrimTest" };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        // Mock file system
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { ("C:\\Music\\TrimTest\\song1.mp3", DateTime.UtcNow), ("C:\\Music\\TrimTest\\song2.mp3", DateTime.UtcNow) });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");

        // Mock metadata extraction to return untrimmed names and mixed casing
        // Song 1: " Artist One " (untrimmed)
        // Song 2: "Artist One" (trimmed)
        // These should resolve to the SAME artist entity
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

        // Act
        var result = await _libraryService.RescanFolderForMusicAsync(folderId);

        // Assert
        result.Should().BeTrue();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            // Verify only ONE artist was created
            var artists = await context.Artists.ToListAsync();
            artists.Should().HaveCount(1);
            artists[0].Name.Should().Be("Artist One");

            // Verify both songs are linked to this artist
            var songs = await context.Songs
                .Include(s => s.SongArtists)
                .ThenInclude(sa => sa.Artist)
                .ToListAsync();

            songs.Should().HaveCount(2);
            songs[0].SongArtists.First().Artist.Name.Should().Be("Artist One");
            songs[1].SongArtists.First().Artist.Name.Should().Be("Artist One");
        }
    }
    [Fact]
    public async Task StartArtistMetadataBackgroundFetchAsync_WithNoUpdates_SavesTimestamps()
    {
        // Arrange
        // We create 15 artists with MetadataLastCheckedUtc = null
        var artists = Enumerable.Range(1, 15).Select(i => new Artist
        {
            Id = Guid.NewGuid(),
            Name = $"Test Artist {i}",
            MetadataLastCheckedUtc = null
        }).ToList();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.AddRange(artists);
            await context.SaveChangesAsync();
        }

        // Enable one provider so the loop doesn't early-exit
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting>
            {
                new() { Id = ServiceProviderIds.LastFm, Order = 0, IsEnabled = true }
            });

        // Mock IServiceScopeFactory so background loop can resolve IDbContextFactory
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IDbContextFactory<MusicDbContext>)).Returns(_dbHelper.ContextFactory);
        _serviceScopeFactory.CreateScope().Returns(scope);

        // Mock the provider to return empty metadata (simulate the condition where no new data is found)
        _lastFmService.GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo())));

        // Act
        var fetchTask = _libraryService.StartArtistMetadataBackgroundFetchAsync();

        // Give the loop time to process the 15 items and flush.
        // The throttling is Task.Delay(50) per item, so 15 items * 50ms = 750ms minimum.
        await Task.Delay(2000);

        // Cancel the background fetch loop
        _libraryService.Dispose();

        // Wait for the background task to complete gracefully
        await fetchTask;

        // Assert
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var dbArtists = await context.Artists.ToListAsync();
            dbArtists.Should().HaveCount(15);
            // Verify ALL 15 artists had their timestamps updated despite having no new metadata
            dbArtists.Should().AllSatisfy(a => a.MetadataLastCheckedUtc.Should().NotBeNull());
        }
    }
}
