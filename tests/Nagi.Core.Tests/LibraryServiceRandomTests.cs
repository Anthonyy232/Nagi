using System;
using System.Threading.Tasks;
using FluentAssertions;
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

public class LibraryServiceRandomTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly LibraryService _libraryService;
    private readonly ProviderPipelineProvider _pipelines;

    public LibraryServiceRandomTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
        var fileSystem = Substitute.For<IFileSystemService>();
        var metadataService = Substitute.For<IMetadataService>();
        var lastFmService = Substitute.For<ILastFmMetadataService>();
        var musicBrainzService = Substitute.For<IMusicBrainzService>();
        var fanartTvService = Substitute.For<IFanartTvService>();
        var driveService = Substitute.For<ITheAudioDbService>();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var pathConfig = Substitute.For<IPathConfiguration>();
        var settingsService = Substitute.For<ISettingsService>();
        var replayGainService = Substitute.For<IReplayGainService>();
        var imageProcessor = Substitute.For<IImageProcessor>();
        var logger = Substitute.For<ILogger<LibraryService>>();
        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ImageDownload);

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            fileSystem,
            metadataService,
            lastFmService,
            musicBrainzService,
            fanartTvService,
            driveService,
            httpClientFactory,
            pathConfig,
            settingsService,
            replayGainService,
            imageProcessor,
            _pipelines,
            logger);
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    public async Task GetRandomAlbumIdAsync_ReturnsAnExistingAlbumOrNullForAnEmptyLibrary(int albumCount)
    {
        var albums = Enumerable.Range(0, albumCount).Select(i => new Album { Title = $"Album {i}" }).ToArray();
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Albums.AddRange(albums);
            await context.SaveChangesAsync();
        }

        var result = await _libraryService.GetRandomAlbumIdAsync();
        if (albumCount == 0)
            result.Should().BeNull();
        else
        {
            result.Should().NotBeNull();
            albums.Select(a => a.Id).Should().Contain(result.Value);
        }
    }

    [Fact]
    public async Task GetRandomFolderIdAsync_IgnoresFoldersWithoutSongs()
    {
        var emptyFolder = new Folder { Name = "Empty", Path = "C:\\Empty" };

        var musicFolder = new Folder { Name = "Music", Path = "C:\\Music" };
        var song = new Song { Title = "Song", Folder = musicFolder, FilePath = "C:\\Music\\song.mp3", DirectoryPath = "C:\\Music" };

        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(emptyFolder);
            context.Folders.Add(musicFolder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

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
