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
    private readonly ISpotifyService _spotifyService;
    private readonly ISettingsService _settingsService;
    private readonly IReplayGainService _replayGainService;
    private readonly IMusicBrainzService _musicBrainzService;
    private readonly IFanartTvService _fanartTvService;
    private readonly ITheAudioDbService _theAudioDbService;
    private readonly IApiKeyService _apiKeyService;
    private readonly IImageProcessor _imageProcessor;

    public LibraryServiceBugTests()
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
        _musicBrainzService = Substitute.For<IMusicBrainzService>();
        _fanartTvService = Substitute.For<IFanartTvService>();
        _theAudioDbService = Substitute.For<ITheAudioDbService>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _imageProcessor = Substitute.For<IImageProcessor>();
        _logger = Substitute.For<ILogger<LibraryService>>();

        _dbHelper = new DbContextFactoryTestHelper();

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

        var songFiles = Enumerable.Range(1, 101).Select(i => $"C:\\Music\\LargeScan\\song{i}.mp3").ToList();
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories).Returns(songFiles);
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
        _fileSystem.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { "C:\\Music\\Scan\\song.mp3" });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");
        _fileSystem.GetLastWriteTimeUtc("C:\\Music\\Scan\\song.mp3").Returns(DateTime.UtcNow);

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

        // Act
        var result = await _libraryService.RescanFolderForMusicAsync(folderId);

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
}
