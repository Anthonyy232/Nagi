using System;
using System.Threading.Tasks;
using System.Net.Http;
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

public class LibraryServiceEventTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IFileSystemService _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
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
    private readonly ILastFmMetadataService _lastFmService;

    public LibraryServiceEventTests()
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
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<LibraryService>>();

        _dbHelper = new DbContextFactoryTestHelper();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

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
        _httpMessageHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AddFolderAsync_FiresLibraryContentChanged_FolderAdded()
    {
        // Arrange
        var folderPath = "C:\\Music\\NewFolder";
        _fileSystem.GetLastWriteTimeUtc(folderPath).Returns(DateTime.UtcNow);
        
        LibraryContentChangedEventArgs? eventArgs = null;
        _libraryService.LibraryContentChanged += (s, e) => eventArgs = e;

        // Act
        await _libraryService.AddFolderAsync(folderPath);

        // Assert
        eventArgs.Should().NotBeNull();
        eventArgs!.ChangeType.Should().Be(LibraryChangeType.FolderAdded);
        eventArgs.FolderId.Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveFolderAsync_FiresLibraryContentChanged_FolderRemoved()
    {
        // Arrange
        var folder = new Folder { Path = "C:\\Music\\FolderToRemove", Name = "FolderToRemove" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        LibraryContentChangedEventArgs? eventArgs = null;
        _libraryService.LibraryContentChanged += (s, e) => eventArgs = e;

        // Act
        await _libraryService.RemoveFolderAsync(folder.Id);

        // Assert
        eventArgs.Should().NotBeNull();
        eventArgs!.ChangeType.Should().Be(LibraryChangeType.FolderRemoved);
        eventArgs.FolderId.Should().Be(folder.Id);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithChanges_FiresLibraryContentChanged_FolderRescanned()
    {
        // Arrange
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\ScanChanges", Name = "ScanChanges" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        // Simulate a new file
        _fileSystem.EnumerateFiles(folder.Path, Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { "C:\\Music\\ScanChanges\\new.mp3" });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");
        _fileSystem.GetLastWriteTimeUtc("C:\\Music\\ScanChanges\\new.mp3").Returns(DateTime.UtcNow);
        
        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new SongFileMetadata { FilePath = "C:\\Music\\ScanChanges\\new.mp3", Title = "New Song" });

        LibraryContentChangedEventArgs? eventArgs = null;
        _libraryService.LibraryContentChanged += (s, e) => eventArgs = e;

        // Act
        await _libraryService.RescanFolderForMusicAsync(folder.Id);

        // Assert
        eventArgs.Should().NotBeNull();
        eventArgs!.ChangeType.Should().Be(LibraryChangeType.FolderRescanned);
        eventArgs.FolderId.Should().Be(folder.Id);
    }

    [Fact]
    public async Task RefreshAllFoldersAsync_WithChanges_FiresLibraryContentChanged_LibraryRescanned()
    {
        // Arrange
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\ScanChanges", Name = "ScanChanges" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFiles(folder.Path, Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { "C:\\Music\\ScanChanges\\new.mp3" });
        _fileSystem.GetExtension(Arg.Any<string>()).Returns(".mp3");
        _fileSystem.GetLastWriteTimeUtc(Arg.Any<string>()).Returns(DateTime.UtcNow);
        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new SongFileMetadata { FilePath = "C:\\Music\\ScanChanges\\new.mp3", Title = "New Song" });

        LibraryContentChangedEventArgs? eventArgs = null;
        _libraryService.LibraryContentChanged += (s, e) => eventArgs = e;

        // Act
        await _libraryService.RefreshAllFoldersAsync();

        // Assert
        eventArgs.Should().NotBeNull();
        eventArgs!.ChangeType.Should().Be(LibraryChangeType.LibraryRescanned);
        eventArgs.FolderId.Should().BeNull();
    }
}
