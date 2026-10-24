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
    private readonly IDeezerService _deezerService;
    private readonly IWikipediaService _wikipediaService;
    private readonly IAlbumCoverService _albumCoverService = Substitute.For<IAlbumCoverService>();
    private readonly IImageProcessor _imageProcessor = Substitute.For<IImageProcessor>();
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly TestHttpMessageHandler _httpHandler = new();

    private readonly LibraryService _libraryService;
    private readonly LibraryReader _libraryReader;

    public ServiceProviderIntegrationTests()
    {
        _settingsService = Substitute.For<ISettingsService>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _lastFmService = Substitute.For<ILastFmMetadataService>();
        _fanartTvService = Substitute.For<IFanartTvService>();
        _theAudioDbService = Substitute.For<ITheAudioDbService>();
        _deezerService = Substitute.For<IDeezerService>();
        _wikipediaService = Substitute.For<IWikipediaService>();
        _dbHelper = new DbContextFactoryTestHelper();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_httpHandler, disposeHandler: false));

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ImageDownload);

        _libraryReader = new LibraryReader(_dbHelper.ContextFactory, Substitute.For<IFileSystemService>(), _settingsService, Substitute.For<ILogger<LibraryReader>>());

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _libraryReader,
            Substitute.For<IFileSystemService>(),
            Substitute.For<IMetadataService>(),
            _lastFmService,
            Substitute.For<IMusicBrainzService>(),
            _fanartTvService,
            _theAudioDbService,
            _deezerService,
            _wikipediaService,
            _albumCoverService,
            httpClientFactory,
            _pathConfig,
            _settingsService,
            Substitute.For<IReplayGainService>(),
            _imageProcessor,
            _pipelines,
            Substitute.For<ILogger<LibraryService>>());

        _pathConfig.ArtistImageCachePath.Returns("C:\\cache\\artistimages");
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _libraryReader.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpHandler.Dispose();
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

    [Fact]
    public async Task LibraryService_UsesDeezerImageOverLastFmWhenNoMbidIsKnown()
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist Without MBID" };
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            context.SaveChanges();
        }

        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting>
            {
                new() { Id = ServiceProviderIds.FanartTv },
                new() { Id = ServiceProviderIds.Deezer },
                new() { Id = ServiceProviderIds.LastFm }
            });
        _deezerService.GetArtistImageUrlAsync(artist.Name, Arg.Any<CancellationToken>())
            .Returns(ServiceResult<string>.FromSuccess("https://deezer.test/artist.jpg"));
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo
            {
                Biography = "Last.fm biography",
                ImageUrl = "https://lastfm.test/artist.jpg"
            }));

        var result = await _libraryService.GetArtistDetailsAsync(artist.Id, true);

        result!.Biography.Should().Be("Last.fm biography");
        _httpHandler.Requests.Select(request => request.RequestUri!.AbsoluteUri)
            .Should().Equal("https://deezer.test/artist.jpg");
    }

    [Theory]
    [InlineData(ServiceResultStatus.Success, true, true)]
    [InlineData(ServiceResultStatus.Success, false, false)]
    [InlineData(ServiceResultStatus.SuccessNotFound, true, true)]
    [InlineData(ServiceResultStatus.TemporaryError, true, false)]
    public async Task MetadataBackgroundFetch_GivesAlbumsWithoutArtAnOnlineCover(ServiceResultStatus status, bool downloads,
        bool expectChecked)
    {
        const string coverPath = @"C:\cache\albumart\cover.light.dark.fetched.jpg";
        var folder = new Folder { Name = "Music", Path = @"C:\Music" };
        var album = new Album { Title = "Album" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = new Artist { Name = "Artist" }, Order = 0 });
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(
                new Song { Title = "One", FilePath = @"C:\Music\1.mp3", Folder = folder, Album = album, MusicBrainzReleaseId = "release-id" },
                new Song { Title = "Two", FilePath = @"C:\Music\2.mp3", Folder = folder, Album = album });
            context.SaveChanges();
        }

        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting> { new() { Id = ServiceProviderIds.CoverArtArchive } });
        _albumCoverService.FindCoverUrlAsync(
                Arg.Is<AlbumCoverQuery>(q => q.Title == "Album" && q.ArtistName == "Artist" && q.ReleaseId == "release-id"),
                Arg.Any<IReadOnlyList<ServiceProviderSetting>>(), Arg.Any<CancellationToken>())
            .Returns(status switch
            {
                ServiceResultStatus.Success => ServiceResult<string>.FromSuccess("https://covers.test/front.jpg"),
                ServiceResultStatus.SuccessNotFound => ServiceResult<string>.FromSuccessNotFound(),
                _ => ServiceResult<string>.FromTemporaryError()
            });
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(
            downloads ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.NotFound)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(Task.FromResult<(string?, string?, string?)>((coverPath, "light", "dark")));
        var changes = new List<LibraryChangeType>();
        _libraryService.LibraryContentChanged += (_, e) => changes.Add(e.ChangeType);

        await _libraryService.StartMetadataBackgroundFetchAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var hasCover = status == ServiceResultStatus.Success && downloads;
        using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var storedAlbum = assertContext.Albums.Single();
        storedAlbum.CoverArtUri.Should().Be(hasCover ? coverPath : null);
        storedAlbum.CoverArtLastCheckedUtc.HasValue.Should().Be(expectChecked);
        assertContext.Songs.Select(s => new { s.AlbumArtUriFromTrack, s.DarkSwatchId }).ToList().Should().AllBeEquivalentTo(
            new { AlbumArtUriFromTrack = hasCover ? coverPath : null, DarkSwatchId = hasCover ? "dark" : null });
        changes.Should().Equal(hasCover ? [LibraryChangeType.SongsUpdated] : []);
    }

    [Fact]
    public async Task MetadataBackgroundFetch_KeepsArtThatAScanFoundDuringTheLookup()
    {
        const string scannedCover = @"C:\cache\albumart\scanned.jpg";
        var album = new Album { Title = "Album" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = new Artist { Name = "Artist" }, Order = 0 });
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(new Song
            {
                Title = "One",
                FilePath = @"C:\Music\1.mp3",
                Folder = new Folder { Name = "Music", Path = @"C:\Music" },
                Album = album
            });
            context.SaveChanges();
        }

        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting> { new() { Id = ServiceProviderIds.CoverArtArchive } });
        _albumCoverService.FindCoverUrlAsync(default!, default!, default).ReturnsForAnyArgs(_ =>
        {
            using var scanContext = _dbHelper.ContextFactory.CreateDbContext();
            scanContext.Albums.Single().CoverArtUri = scannedCover;
            scanContext.SaveChanges();
            return ServiceResult<string>.FromSuccess("https://covers.test/front.jpg");
        });
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(Task.FromResult<(string?, string?, string?)>((@"C:\cache\albumart\online.jpg", "light", "dark")));

        await _libraryService.StartMetadataBackgroundFetchAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        assertContext.Albums.Single().CoverArtUri.Should().Be(scannedCover);
        assertContext.Albums.Single().CoverArtLastCheckedUtc.Should().NotBeNull();
        assertContext.Songs.Single().AlbumArtUriFromTrack.Should().BeNull();
    }
}
