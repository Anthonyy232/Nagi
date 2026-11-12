using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.OnlineProviders;
using Nagi.Core.OnlineProviders.Pipelines;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using NSubstitute;

namespace Nagi.Core.Tests.Utils;

/// <summary>
///     The library services over a test database, with substitutes for everything outside the library.
/// </summary>
public abstract class LibraryTestBase : IDisposable
{
    protected readonly DbContextFactoryTestHelper _dbHelper;

    protected readonly IFileSystemService _fileSystem;
    protected readonly IHttpClientFactory _httpClientFactory;
    protected readonly TestHttpMessageHandler _httpMessageHandler;
    protected readonly ILastFmMetadataService _lastFmService;

    protected readonly TestLibrary _library;
    protected readonly LibraryReader _libraryReader;

    private readonly ILogger<ArtistMetadataService> _artistMetadataLogger = Substitute.For<ILogger<ArtistMetadataService>>();

    protected readonly IMetadataService _metadataService;
    protected readonly IPathConfiguration _pathConfig;
    protected readonly SettingsService _settingsService;
    protected readonly IReplayGainService _replayGainService;
    protected readonly IMusicBrainzService _musicBrainzService;
    protected readonly IFanartTvService _fanartTvService;
    protected readonly ITheAudioDbService _theAudioDbService;
    protected readonly IDeezerService _deezerService = Substitute.For<IDeezerService>();
    protected readonly IAlbumCoverService _albumCoverService = Substitute.For<IAlbumCoverService>();
    protected readonly IImageProcessor _imageProcessor;
    private readonly ProviderPipelineProvider _pipelines;

    protected LibraryTestBase(bool useFileDatabase = false)
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _fileSystem.NormalizePath(Arg.Any<string>()).Returns(call => PathCanonicalizer.Normalize(call.Arg<string>()));
        _metadataService = Substitute.For<IMetadataService>();
        _lastFmService = Substitute.For<ILastFmMetadataService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _settingsService = TestSettings.Create();
        _replayGainService = Substitute.For<IReplayGainService>();
        _musicBrainzService = Substitute.For<IMusicBrainzService>();
        _fanartTvService = Substitute.For<IFanartTvService>();
        _theAudioDbService = Substitute.For<ITheAudioDbService>();
        _imageProcessor = Substitute.For<IImageProcessor>();
        _httpMessageHandler = new TestHttpMessageHandler();

        _dbHelper = new DbContextFactoryTestHelper(useFileDatabase);

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _pathConfig.AlbumArtCachePath.Returns("C:\\cache\\albumart");
        _pathConfig.ArtistImageCachePath.Returns("C:\\cache\\artistimages");
        _pathConfig.PlaylistImageCachePath.Returns("C:\\cache\\playlistimages");
        _pathConfig.LrcCachePath.Returns("C:\\cache\\lrc");
        _pathConfig.LibraryCacheFolders.Returns(["C:\\cache\\albumart", "C:\\cache\\artistimages",
            "C:\\cache\\playlistimages", "C:\\cache\\lrc"]);

        _pipelines = TestProviderPipeline.Build(ProviderCatalog.ImageDownload.ProviderId);

        _libraryReader = new LibraryReader(_dbHelper.ContextFactory, _fileSystem, _settingsService);

        _library = new TestLibrary(
            _dbHelper.ContextFactory,
            _fileSystem,
            _metadataService,
            new ArtistMetadataService(
                _lastFmService,
                _musicBrainzService,
                _fanartTvService,
                _theAudioDbService,
                _deezerService,
                Substitute.For<IWikipediaService>(),
                _settingsService,
                _artistMetadataLogger),
            _albumCoverService,
            _httpClientFactory,
            _pathConfig,
            _settingsService,
            _replayGainService,
            _imageProcessor,
            _pipelines);
    }

    public void Dispose()
    {
        _library.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
        _httpMessageHandler.Dispose();
    }

    // Concurrency tests use a file database, whose writers wait for each other the way the app's do; the shared
    // in-memory database locks whole tables instead.
    protected TestLibrary CreateLibrary(DbContextFactoryTestHelper database) =>
        new(database.ContextFactory, _fileSystem, _metadataService,
            new ArtistMetadataService(_lastFmService, _musicBrainzService, _fanartTvService, _theAudioDbService,
                _deezerService, Substitute.For<IWikipediaService>(), _settingsService, _artistMetadataLogger),
            _albumCoverService, _httpClientFactory, _pathConfig, _settingsService, _replayGainService,
            _imageProcessor, _pipelines);
}
