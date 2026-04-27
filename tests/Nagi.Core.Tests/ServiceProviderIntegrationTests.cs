using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Provides integration tests to verify that service provider enabling, disabling,
///     and reordering (priority) logic works correctly across different services.
/// </summary>
public class ServiceProviderIntegrationTests : IDisposable
{
    private readonly ProviderPipelineProvider _pipelines;
    private readonly IFileSystemService _fileSystem;
    private readonly ISettingsService _settingsService;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILibraryWriter _libraryWriter;
    private readonly IOnlineLyricsService _lrcLibService;
    private readonly INetEaseLyricsService _netEaseLyricsService;
    private readonly ILastFmMetadataService _lastFmService;
    private readonly IMusicBrainzService _musicBrainzService;
    private readonly IFanartTvService _fanartTvService;
    private readonly ITheAudioDbService _theAudioDbService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IReplayGainService _replayGainService;
    private readonly IApiKeyService _apiKeyService;
    private readonly IMetadataService _metadataService;
    private readonly IImageProcessor _imageProcessor;
    private readonly DbContextFactoryTestHelper _dbHelper;

    private readonly LrcService _lrcService;
    private readonly LibraryService _libraryService;

    public ServiceProviderIntegrationTests()
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _settingsService = Substitute.For<ISettingsService>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _libraryWriter = Substitute.For<ILibraryWriter>();
        _lrcLibService = Substitute.For<IOnlineLyricsService>();
        _netEaseLyricsService = Substitute.For<INetEaseLyricsService>();
        _lastFmService = Substitute.For<ILastFmMetadataService>();
        _musicBrainzService = Substitute.For<IMusicBrainzService>();
        _fanartTvService = Substitute.For<IFanartTvService>();
        _theAudioDbService = Substitute.For<ITheAudioDbService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _serviceScopeFactory = Substitute.For<IServiceScopeFactory>();
        _replayGainService = Substitute.For<IReplayGainService>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _metadataService = Substitute.For<IMetadataService>();
        _imageProcessor = Substitute.For<IImageProcessor>();
        _dbHelper = new DbContextFactoryTestHelper();

        var loggerFactory = Substitute.For<ILoggerFactory>();

        _lrcService = new LrcService(
            _fileSystem,
            _lrcLibService,
            _netEaseLyricsService,
            _settingsService,
            _pathConfig,
            _libraryWriter,
            Substitute.For<ILogger<LrcService>>());

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
            Substitute.For<ILogger<LibraryService>>());

        _pathConfig.ArtistImageCachePath.Returns("C:\\cache\\artistimages");
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LrcService_RespectsProviderOrder()
    {
        // Arrange
        var song = new Song { Title = "Test Song", Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);

        // NetEase is first, LRCLIB is second
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics)
            .Returns(new List<ServiceProviderSetting>
            {
                new() { Id = ServiceProviderIds.NetEase, Order = 0, IsEnabled = true },
                new() { Id = ServiceProviderIds.LrcLib, Order = 1, IsEnabled = true }
            });

        _netEaseLyricsService.SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]From NetEase");
        _lrcLibService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]From LRCLIB");

        // Act
        var result = await _lrcService.GetLyricsAsync(song);

        // Assert
        result.Should().NotBeNull();
        result!.Lines[0].Text.Should().Be("From NetEase");
    }

    [Fact]
    public async Task LrcService_SkipsDisabledProviders()
    {
        // Arrange
        var song = new Song { Title = "Test Song", Duration = TimeSpan.FromMinutes(3) };
        _settingsService.GetFetchOnlineLyricsEnabledAsync().Returns(true);

        // NetEase is disabled, LRCLIB is enabled
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics)
            .Returns(new List<ServiceProviderSetting>
            {
                new() { Id = ServiceProviderIds.LrcLib, Order = 0, IsEnabled = true }
            });

        _lrcLibService.GetLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns("[00:01.00]From LRCLIB");

        // Act
        var result = await _lrcService.GetLyricsAsync(song);

        // Assert
        result.Should().NotBeNull();
        result!.Lines[0].Text.Should().Be("From LRCLIB");
        await _netEaseLyricsService.DidNotReceive().SearchLyricsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LibraryService_WhenNoProvidersEnabled_DoesNotSetMetadataLastChecked()
    {
        // Arrange
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Test Artist" };
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            context.SaveChanges();
        }

        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting>()); // Empty list

        // Act
        await _libraryService.GetArtistDetailsAsync(artist.Id, true);

        // Assert - MetadataLastCheckedUtc should NOT be set
        using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var assertArtist = await assertContext.Artists.FindAsync(artist.Id);
        assertArtist!.MetadataLastCheckedUtc.Should().BeNull();
    }

    [Fact]
    public async Task LibraryService_OnlyCallsMbidDependentProvidersIfMbidIsAvailable()
    {
        // Arrange
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist Without MBID", MusicBrainzId = null };
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            context.SaveChanges();
        }

        // MusicBrainz is DISABLED, but TheAudioDB is ENABLED
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting>
            {
                new() { Id = ServiceProviderIds.TheAudioDb, Order = 0, IsEnabled = true }
            });

        // Act
        await _libraryService.GetArtistDetailsAsync(artist.Id, true);

        // Assert - TheAudioDB should NOT be called because MBID is missing and MB lookup is disabled
        await _theAudioDbService.DidNotReceive().GetArtistMetadataAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
