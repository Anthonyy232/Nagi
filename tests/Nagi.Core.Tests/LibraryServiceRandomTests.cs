using System;
using System.Threading.Tasks;
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

#pragma warning disable NS2002 // NSubstitute.Analyzers false positives for some interfaces

namespace Nagi.Core.Tests;

public class LibraryServiceRandomTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly LibraryService _libraryService;
    private readonly IFileSystemService _fileSystem;

    public LibraryServiceRandomTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
        _fileSystem = Substitute.For<IFileSystemService>();
        var metadataService = Substitute.For<IMetadataService>();
        var lastFmService = Substitute.For<ILastFmMetadataService>();
        var spotifyService = Substitute.For<ISpotifyService>();
        var musicBrainzService = Substitute.For<IMusicBrainzService>();
        var fanartTvService = Substitute.For<IFanartTvService>();
        var driveService = Substitute.For<ITheAudioDbService>();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
#pragma warning disable NS2002
        var pathConfig = Substitute.For<IPathConfiguration>();
#pragma warning restore NS2002
        var settingsService = Substitute.For<ISettingsService>();
        var replayGainService = Substitute.For<IReplayGainService>();
        var apiKeyService = Substitute.For<IApiKeyService>();
        var imageProcessor = Substitute.For<IImageProcessor>();
        var logger = Substitute.For<ILogger<LibraryService>>();

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _fileSystem,
            metadataService,
            lastFmService,
            spotifyService,
            musicBrainzService,
            fanartTvService,
            driveService,
            httpClientFactory,
            serviceScopeFactory,
            pathConfig,
            settingsService,
            replayGainService,
            apiKeyService,
            imageProcessor,
            logger);
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _dbHelper.Dispose();
    }

    [Fact]
    public async Task GetRandomAlbumIdAsync_WithEmptyDatabase_ReturnsNull()
    {
        var result = await _libraryService.GetRandomAlbumIdAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetRandomAlbumIdAsync_WithSingleItem_ReturnsThatItemId()
    {
        var album = new Album { Title = "Test Album" };
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Albums.Add(album);
            await context.SaveChangesAsync();
        }

        var result = await _libraryService.GetRandomAlbumIdAsync();
        result.Should().Be(album.Id);
    }

    [Fact]
    public async Task GetRandomAlbumIdAsync_WithMultipleItems_ReturnsValidId()
    {
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            for (int i = 0; i < 10; i++)
            {
                context.Albums.Add(new Album { Title = $"Album {i}" });
            }
            await context.SaveChangesAsync();
        }

        var result = await _libraryService.GetRandomAlbumIdAsync();
        result.Should().NotBeNull();
        
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var exists = await context.Albums.AnyAsync(a => a.Id == result);
            exists.Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetRandomFolderIdAsync_IgnoresFoldersWithoutSongs()
    {
        // 1. Folder with no songs
        var emptyFolder = new Folder { Name = "Empty", Path = "C:\\Empty" };
        
        // 2. Folder with songs
        var musicFolder = new Folder { Name = "Music", Path = "C:\\Music" };
        var song = new Song { Title = "Song", Folder = musicFolder, FilePath = "C:\\Music\\song.mp3", DirectoryPath = "C:\\Music" };

        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(emptyFolder);
            context.Folders.Add(musicFolder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        // Act: Try to get a random folder multiple times to ensure we never get the empty one
        for (int i = 0; i < 5; i++)
        {
            var result = await _libraryService.GetRandomFolderIdAsync();
            result.Should().Be(musicFolder.Id);
        }
    }
    
    [Fact]
    public async Task GetPlaylistCountAsync_ReturnsCorrectCount()
    {
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Playlists.Add(new Playlist { Name = "P1" });
            context.Playlists.Add(new Playlist { Name = "P2" });
            context.Playlists.Add(new Playlist { Name = "P3" });
            await context.SaveChangesAsync();
        }

        var count = await _libraryService.GetPlaylistCountAsync();
        count.Should().Be(3);
    }
}
