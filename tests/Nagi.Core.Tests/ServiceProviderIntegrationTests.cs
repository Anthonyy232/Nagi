using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class ServiceProviderIntegrationTests : IDisposable
{
    private readonly ProviderPipelineProvider _pipelines;
    private readonly ISettingsService _settingsService;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILastFmMetadataService _lastFmService;
    private readonly IFanartTvService _fanartTvService;
    private readonly ITheAudioDbService _theAudioDbService;
    private readonly DbContextFactoryTestHelper _dbHelper;

    private readonly LibraryService _libraryService;

    public ServiceProviderIntegrationTests()
    {
        _settingsService = Substitute.For<ISettingsService>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _lastFmService = Substitute.For<ILastFmMetadataService>();
        _fanartTvService = Substitute.For<IFanartTvService>();
        _theAudioDbService = Substitute.For<ITheAudioDbService>();
        _dbHelper = new DbContextFactoryTestHelper();

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ImageDownload);

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            Substitute.For<IFileSystemService>(),
            Substitute.For<IMetadataService>(),
            _lastFmService,
            Substitute.For<IMusicBrainzService>(),
            _fanartTvService,
            _theAudioDbService,
            Substitute.For<IHttpClientFactory>(),
            _pathConfig,
            _settingsService,
            Substitute.For<IReplayGainService>(),
            Substitute.For<IImageProcessor>(),
            _pipelines,
            Substitute.For<ILogger<LibraryService>>());

        _pathConfig.ArtistImageCachePath.Returns("C:\\cache\\artistimages");
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
    }

    [Fact]
    public async Task LibraryService_WhenNoProvidersEnabled_DoesNotSetMetadataLastChecked()
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Test Artist" };
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            context.SaveChanges();
        }

        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting>());

        await _libraryService.GetArtistDetailsAsync(artist.Id, true);

        using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var assertArtist = await assertContext.Artists.FindAsync(artist.Id);
        assertArtist!.MetadataLastCheckedUtc.Should().BeNull();
    }

    [Fact]
    public async Task LibraryService_OnlyCallsMbidDependentProvidersIfMbidIsAvailable()
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist Without MBID", MusicBrainzId = null };
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            context.SaveChanges();
        }

        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting>
            {
                new() { Id = ServiceProviderIds.TheAudioDb, Order = 0, IsEnabled = true },
                new() { Id = ServiceProviderIds.FanartTv, Order = 1, IsEnabled = true },
                new() { Id = ServiceProviderIds.LastFm, Order = 2, IsEnabled = true }
            });
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo { Biography = "Available biography" }));

        var result = await _libraryService.GetArtistDetailsAsync(artist.Id, true);
        result!.Biography.Should().Be("Available biography");

        await _theAudioDbService.DidNotReceive().GetArtistMetadataAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _fanartTvService.DidNotReceive().GetArtistImagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
