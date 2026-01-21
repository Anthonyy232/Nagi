using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;
using System.IO;

namespace Nagi.Core.Tests;

public class MultiArtistTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IFileSystemService _fileSystem;
    private readonly IMetadataService _metadataService;
    private readonly LibraryService _libraryService;
    private readonly ITestOutputHelper _output;
    private Exception? _capturedLoggerException;

    public MultiArtistTests(ITestOutputHelper output)
    {
        _output = output;
        _dbHelper = new DbContextFactoryTestHelper();
        _fileSystem = Substitute.For<IFileSystemService>();
        _metadataService = Substitute.For<IMetadataService>();

        // Minimal mock setup for LibraryService dependency injection
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var settingsService = Substitute.For<ISettingsService>();
        var pathConfig = Substitute.For<IPathConfiguration>();
        pathConfig.AlbumArtCachePath.Returns("C:\\cache\\albumart");
        pathConfig.ArtistImageCachePath.Returns("C:\\cache\\artistimages");
        pathConfig.LrcCachePath.Returns("C:\\cache\\lrc");
        
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();

        // Set up logger to capture exceptions
        var logger = Substitute.For<ILogger<LibraryService>>();
        logger.WhenForAnyArgs(x => x.Log(
            Arg.Any<LogLevel>(), 
            Arg.Any<EventId>(), 
            Arg.Any<object>(), 
            Arg.Any<Exception?>(), 
            Arg.Any<Func<object, Exception?, string>>()))
            .Do(callInfo =>
            {
                var ex = callInfo.ArgAt<Exception?>(3);
                if (ex != null)
                {
                    _capturedLoggerException = ex;
                    _output.WriteLine($"LOGGED EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                    _output.WriteLine($"Stack Trace: {ex.StackTrace}");
                }
            });

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _fileSystem,
            _metadataService,
            Substitute.For<ILastFmMetadataService>(),
            Substitute.For<ISpotifyService>(),
            Substitute.For<IMusicBrainzService>(),
            Substitute.For<IFanartTvService>(),
            Substitute.For<ITheAudioDbService>(),
            httpClientFactory,
            serviceScopeFactory,
            pathConfig,
            settingsService,
            Substitute.For<IReplayGainService>(),
            Substitute.For<IApiKeyService>(),
            Substitute.For<IImageProcessor>(),
            logger
        );
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _dbHelper.Dispose();
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithMultipleArtists_CorrectlyPopulatesDenormalizedFields()
    {
        // Reset captured exception from any previous test
        _capturedLoggerException = null;
        
        // Arrange
        var folderId = Guid.NewGuid();
        var folderPath = "C:\\Music\\MultiArtist";
        var filePath = Path.Combine(folderPath, "song.mp3");

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(new Folder { Id = folderId, Path = folderPath, Name = "MultiArtist" });
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folderPath).Returns(true);
        _fileSystem.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories).Returns(new[] { filePath });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");
        _fileSystem.GetLastWriteTimeUtc(Arg.Any<string>()).Returns(DateTime.UtcNow);
        _fileSystem.GetFileNameWithoutExtension(filePath).Returns("song");

        var metadata = new SongFileMetadata
        {
            FilePath = filePath,
            Title = "Collaboration Song",
            Artists = new List<string> { "Primary Artist", "Featured Artist" },
            AlbumArtists = new List<string> { "Primary Artist" },
            Album = "Collab Album",
            Duration = TimeSpan.FromMinutes(3)
        };

        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(metadata);

        // Act
        var result = await _libraryService.RescanFolderForMusicAsync(folderId);
        
        // If scan failed, re-throw the captured exception with context
        if (!result && _capturedLoggerException != null)
        {
            throw new Exception($"Scan failed with exception: {_capturedLoggerException.Message}", _capturedLoggerException);
        }
        
        // Verify the scan succeeded
        result.Should().BeTrue("the scan should complete without errors");

        // Assert
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
            
            // Check Song denormalized fields
            song!.ArtistName.Should().Be("Primary Artist & Featured Artist");
            song.PrimaryArtistName.Should().Be("Primary Artist");

            // Check Album denormalized fields
            song.Album.Should().NotBeNull();
            song.Album!.ArtistName.Should().Be("Primary Artist"); // AlbumArtists was just "Primary Artist" in setup
            song.Album.PrimaryArtistName.Should().Be("Primary Artist");
        }
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_SingleArtist_PopulatesFieldsCorrectly()
    {
        // Reset captured exception from any previous test
        _capturedLoggerException = null;
        
        // Arrange
        var folderId = Guid.NewGuid();
        var folderPath = "C:\\Music\\SingleArtist";
        var filePath = Path.Combine(folderPath, "solo.mp3");

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(new Folder { Id = folderId, Path = folderPath, Name = "SingleArtist" });
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folderPath).Returns(true);
        _fileSystem.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories).Returns(new[] { filePath });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");
        _fileSystem.GetLastWriteTimeUtc(Arg.Any<string>()).Returns(DateTime.UtcNow);

        var metadata = new SongFileMetadata
        {
            FilePath = filePath,
            Title = "Solo Song",
            Artists = new List<string> { "Solo Artist" },
            Album = "Solo Album",
            Duration = TimeSpan.FromMinutes(3)
        };

        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(metadata);

        // Act
        var result = await _libraryService.RescanFolderForMusicAsync(folderId);
        
        // If scan failed, re-throw the captured exception with context
        if (!result && _capturedLoggerException != null)
        {
            throw new Exception($"Scan failed with exception: {_capturedLoggerException.Message}", _capturedLoggerException);
        }
        
        // Verify the scan succeeded (no exception caught)
        result.Should().BeTrue("the scan should complete without errors");

        // Assert
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await context.Songs.FirstOrDefaultAsync(s => s.FilePath == filePath);
            
            song.Should().NotBeNull();
            song!.ArtistName.Should().Be("Solo Artist");
            song.PrimaryArtistName.Should().Be("Solo Artist");
        }
    }

    [Fact]
    public void Song_SyncDenormalizedFields_CorrectlyJoinsArtists()
    {
        // Arrange
        var song = new Song { Title = "Test" };
        song.SongArtists.Add(new SongArtist { Order = 1, Artist = new Artist { Name = "Artist B" } });
        song.SongArtists.Add(new SongArtist { Order = 0, Artist = new Artist { Name = "Artist A" } });

        // Act
        song.SyncDenormalizedFields();

        // Assert
        song.ArtistName.Should().Be("Artist A & Artist B");
        song.PrimaryArtistName.Should().Be("Artist A");
    }

    [Fact]
    public void Album_SyncDenormalizedFields_CorrectlyJoinsArtists()
    {
        // Arrange
        var album = new Album { Title = "Test Album" };
        album.AlbumArtists.Add(new AlbumArtist { Order = 0, Artist = new Artist { Name = "Main Artist" } });
        album.AlbumArtists.Add(new AlbumArtist { Order = 1, Artist = new Artist { Name = "Feature Artist" } });

        // Act
        album.SyncDenormalizedFields();

        // Assert
        album.ArtistName.Should().Be("Main Artist & Feature Artist");
        album.PrimaryArtistName.Should().Be("Main Artist");
    }

    [Fact]
    public async Task PlayTransientFileAsync_PopulatesDenormalizedFields()
    {
        // Arrange
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

        var playbackService = new MusicPlaybackService(
            Substitute.For<ISettingsService>(),
            Substitute.For<IAudioPlayer>(),
            Substitute.For<ILibraryService>(),
            _metadataService,
            Substitute.For<ILogger<MusicPlaybackService>>()
        );

        // Act
        await playbackService.PlayTransientFileAsync(filePath);

        // Assert
        playbackService.CurrentTrack.Should().NotBeNull();
        playbackService.CurrentTrack!.ArtistName.Should().Be("Artist 1 & Artist 2");
        playbackService.CurrentTrack.PrimaryArtistName.Should().Be("Artist 1");
        
        playbackService.CurrentTrack.Album.Should().NotBeNull();
        playbackService.CurrentTrack.Album!.ArtistName.Should().Be("Artist 1");
        playbackService.CurrentTrack.Album.PrimaryArtistName.Should().Be("Artist 1");
    }

    [Fact]
    public async Task AddSongWithDetailsAsync_CorrectlyPopulatesDenormalizedFields()
    {
        // Arrange
        var folderId = Guid.NewGuid();
        var metadata = new SongFileMetadata
        {
            FilePath = "C:\\Music\\AddSong.mp3",
            Title = "Added Song",
            Artists = new List<string> { "Singer A", "Singer B" },
            Album = "Added Album",
            Duration = TimeSpan.FromMinutes(3)
        };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(new Folder { Id = folderId, Path = "C:\\Music" });
            await context.SaveChangesAsync();
        }

        // Act
        var addedSong = await _libraryService.AddSongWithDetailsAsync(folderId, metadata);

        // Assert
        addedSong.Should().NotBeNull();
        addedSong!.ArtistName.Should().Be("Singer A & Singer B");
        addedSong.PrimaryArtistName.Should().Be("Singer A");

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var album = await context.Albums.FirstOrDefaultAsync(a => a.Title == "Added Album");
            album.Should().NotBeNull();
            album!.ArtistName.Should().Be("Singer A & Singer B"); // In this method, AlbumArtists defaults to trackArtists
            album.PrimaryArtistName.Should().Be("Singer A");
        }
    }
}
