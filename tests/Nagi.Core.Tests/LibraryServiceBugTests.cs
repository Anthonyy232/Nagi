using FluentAssertions;
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
                Artist = "Artist" 
            }));

        // Act & Assert
        // This should not throw System.ArgumentException: AggressiveGC requires setting the blocking parameter to true.
        Func<Task> act = async () => await _libraryService.RescanFolderForMusicAsync(folder.Id);
        await act.Should().NotThrowAsync<ArgumentException>();
    }
}
