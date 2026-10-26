using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

public partial class LibraryServiceTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;

    private readonly IFileSystemService _fileSystem;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILastFmMetadataService _lastFmService;

    private readonly LibraryService _libraryService;
    private readonly LibraryReader _libraryReader;

    private readonly ILogger<LibraryService> _logger;
    private readonly ILogger<ArtistMetadataService> _artistMetadataLogger = Substitute.For<ILogger<ArtistMetadataService>>();

    private readonly IMetadataService _metadataService;
    private readonly IPathConfiguration _pathConfig;
    private readonly ISettingsService _settingsService;
    private readonly IReplayGainService _replayGainService;
    private readonly IMusicBrainzService _musicBrainzService;
    private readonly IFanartTvService _fanartTvService;
    private readonly ITheAudioDbService _theAudioDbService;
    private readonly IImageProcessor _imageProcessor;
    private readonly ProviderPipelineProvider _pipelines;

    public LibraryServiceTests()
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _fileSystem.NormalizePath(Arg.Any<string>()).Returns(call => PathCanonicalizer.Normalize(call.Arg<string>()));
        _metadataService = Substitute.For<IMetadataService>();
        _lastFmService = Substitute.For<ILastFmMetadataService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _pathConfig = Substitute.For<IPathConfiguration>();
        _settingsService = Substitute.For<ISettingsService>();
        _replayGainService = Substitute.For<IReplayGainService>();
        _musicBrainzService = Substitute.For<IMusicBrainzService>();
        _fanartTvService = Substitute.For<IFanartTvService>();
        _theAudioDbService = Substitute.For<ITheAudioDbService>();
        _imageProcessor = Substitute.For<IImageProcessor>();
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<LibraryService>>();

        _dbHelper = new DbContextFactoryTestHelper();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _pathConfig.AlbumArtCachePath.Returns("C:\\cache\\albumart");
        _pathConfig.ArtistImageCachePath.Returns("C:\\cache\\artistimages");
        _pathConfig.PlaylistImageCachePath.Returns("C:\\cache\\playlistimages");
        _pathConfig.LrcCachePath.Returns("C:\\cache\\lrc");

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ImageDownload);

        _libraryReader = new LibraryReader(_dbHelper.ContextFactory, _fileSystem, _settingsService, Substitute.For<ILogger<LibraryReader>>());

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            _libraryReader,
            _fileSystem,
            _metadataService,
            new ArtistMetadataService(
                _lastFmService,
                _musicBrainzService,
                _fanartTvService,
                _theAudioDbService,
                Substitute.For<IDeezerService>(),
                Substitute.For<IWikipediaService>(),
                _artistMetadataLogger),
            Substitute.For<IAlbumCoverService>(),
            _httpClientFactory,
            _pathConfig,
            _settingsService,
            _replayGainService,
            _imageProcessor,
            _pipelines,
            _logger);
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _libraryReader.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
        _httpMessageHandler.Dispose();
    }

    [Theory]
    [InlineData(ServiceProviderIds.LastFm)]
    [InlineData(ServiceProviderIds.TheAudioDb)]
    [InlineData(ServiceProviderIds.FanartTv)]
    [InlineData(ServiceProviderIds.MusicBrainz)]
    public async Task ArtistMetadata_TemporaryProviderFailure_RemainsRetryable(string providerId)
    {
        var artist = new Artist
        {
            Name = "Retry artist",
            MusicBrainzId = providerId == ServiceProviderIds.MusicBrainz ? null : Guid.NewGuid().ToString()
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata).Returns(
            new List<ServiceProviderSetting> { new() { Id = providerId, IsEnabled = true } });
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
            ServiceResult<ArtistInfo>.FromTemporaryError(), ServiceResult<ArtistInfo>.FromSuccessNotFound());
        _theAudioDbService.GetArtistMetadataAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
            ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError(), ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound());
        _fanartTvService.GetArtistImagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            ServiceResult<FanartTvArtistImages>.FromTemporaryError(), ServiceResult<FanartTvArtistImages>.FromSuccessNotFound());
        _musicBrainzService.SearchArtistAsync(artist.Name, Arg.Any<CancellationToken>()).Returns(
            ServiceResult<string>.FromTemporaryError(), ServiceResult<string>.FromSuccessNotFound());

        var failed = await _libraryService.GetArtistDetailsAsync(artist.Id, true);
        failed!.MetadataLastCheckedUtc.Should().BeNull();

        var retried = await _libraryService.GetArtistDetailsAsync(artist.Id, true);
        retried!.MetadataLastCheckedUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData(ServiceProviderIds.TheAudioDb)]
    [InlineData(ServiceProviderIds.MusicBrainz)]
    public async Task ArtistMetadata_ProviderTimeout_UsesLowerPriorityBiography(string providerId)
    {
        var artist = new Artist
        {
            Name = "Fallback artist",
            MusicBrainzId = providerId == ServiceProviderIds.MusicBrainz ? null : Guid.NewGuid().ToString()
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata).Returns(
            new List<ServiceProviderSetting>
            {
                new() { Id = providerId, IsEnabled = true },
                new() { Id = ServiceProviderIds.LastFm, IsEnabled = true }
            });
        _theAudioDbService.GetArtistMetadataAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ServiceResult<TheAudioDbArtistInfo>>(new TaskCanceledException("Timed out")));
        _musicBrainzService.SearchArtistAsync(artist.Name, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ServiceResult<string>>(new TaskCanceledException("Timed out")));
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo { Biography = "Fallback biography" }));

        var result = await _libraryService.GetArtistDetailsAsync(artist.Id, true);

        result!.Biography.Should().Be("Fallback biography");
        result.MetadataLastCheckedUtc.Should().BeNull();
    }

    [Fact]
    public async Task ArtistMetadata_CancellationDuringIdentityLookup_StillObservesOtherProviderFailures()
    {
        var artist = new Artist { Name = "Cancelled lookup" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata).Returns(
            new List<ServiceProviderSetting>
            {
                new() { Id = ServiceProviderIds.MusicBrainz, IsEnabled = true },
                new() { Id = ServiceProviderIds.LastFm, IsEnabled = true }
            });
        var identity = new TaskCompletionSource<ServiceResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<ServiceResult<ArtistInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new HttpRequestException("Late provider failure");
        _musicBrainzService.SearchArtistAsync(artist.Name, Arg.Any<CancellationToken>()).Returns(identity.Task);
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            started.TrySetResult();
            return metadata.Task;
        });
        _artistMetadataLogger.When(logger => logger.Log(LogLevel.Debug, Arg.Any<EventId>(), Arg.Any<Arg.AnyType>(),
                Arg.Is<Exception?>(exception => ReferenceEquals(exception, failure)),
                Arg.Any<Func<Arg.AnyType, Exception?, string>>()))
            .Do(_ => observed.TrySetResult());
        using var cancellation = new CancellationTokenSource();

        var lookup = _libraryService.GetArtistDetailsAsync(artist.Id, true, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        identity.SetCanceled(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        metadata.SetException(failure);

        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ArtistMetadata_FailedImageDownload_RetriesWithoutLosingBiography()
    {
        var artist = new Artist { Name = "Image retry artist" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata).Returns(
            new List<ServiceProviderSetting> { new() { Id = ServiceProviderIds.LastFm, IsEnabled = true } });
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo
            {
                Biography = "Biography", ImageUrl = "https://example.com/artist.jpg"
            }));
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_httpMessageHandler, disposeHandler: false));
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var failed = await _libraryService.GetArtistDetailsAsync(artist.Id, true);
        failed!.Biography.Should().Be("Biography");
        failed.LocalImageCachePath.Should().BeNull();
        failed.MetadataLastCheckedUtc.Should().BeNull();

        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        _imageProcessor.ProcessImageBytesAsync(Arg.Any<byte[]>()).Returns(new byte[] { 4, 5, 6 });
        var retried = await _libraryService.GetArtistDetailsAsync(artist.Id, true);

        retried!.Biography.Should().Be("Biography");
        retried.LocalImageCachePath.Should().NotBeNullOrEmpty();
        retried.MetadataLastCheckedUtc.Should().NotBeNull();
        _fileSystem.Received(1).MoveFile(Arg.Is<string>(path => path.EndsWith(".tmp")), retried.LocalImageCachePath!, true);
        await _fileSystem.DidNotReceive().WriteAllBytesAsync(retried.LocalImageCachePath!, Arg.Any<byte[]>());
    }

    [Theory]
    [InlineData("dispose")]
    [InlineData("disable")]
    [InlineData("reset")]
    public async Task BackgroundMetadata_CancellationLetsActiveRunUnwind(string action)
    {
        _settingsService.GetFetchOnlineMetadataEnabledAsync().Returns(true);
        var artist = new Artist { Name = "Pending artist" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting> { new() { Id = ServiceProviderIds.LastFm } });
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TaskCompletionSource<ServiceResult<ArtistInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            started.TrySetResult(call.Arg<CancellationToken>());
            return provider.Task;
        });

        var run = _libraryService.StartMetadataBackgroundFetchAsync();
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task reset = Task.CompletedTask;
        if (action == "dispose") _libraryService.Dispose();
        else if (action == "disable") _settingsService.FetchOnlineMetadataEnabledChanged += Raise.Event<Action<bool>>(false);
        else
        {
            reset = _libraryService.ClearAllLibraryDataAsync();
            reset.IsCompleted.Should().BeFalse();
        }
        token.IsCancellationRequested.Should().BeTrue();
        provider.SetResult(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo { Biography = "Late result" }));
        await Task.WhenAll(run, reset).WaitAsync(TimeSpan.FromSeconds(5));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var stored = await context.Artists.FindAsync(artist.Id);
            if (action == "reset") stored.Should().BeNull();
            else
            {
                stored!.Biography.Should().BeNull();
                stored.MetadataLastCheckedUtc.Should().BeNull();
            }
        }
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccessNotFound());
        await _libraryService.StartMetadataBackgroundFetchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await _lastFmService.Received(action == "disable" ? 2 : 1)
            .GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_DuringScanOrReplayGain_AllowsWorkerToReleaseItsGate(bool duringReplayGain)
    {
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }
        const string songPath = @"C:\Music\song.mp3";
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (songPath, DateTime.UtcNow) });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<SongFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var songMetadata = new SongFileMetadata { FilePath = songPath, Title = "Song", Artists = ["Artist"] };
        _settingsService.GetVolumeNormalizationEnabledAsync().Returns(duringReplayGain);
        _replayGainService.ScanLibraryAsync(Arg.Any<IProgress<ScanProgress>?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
        });
        _metadataService.ExtractMetadataAsync(songPath, folder.Path).Returns(_ =>
        {
            if (duringReplayGain) return Task.FromResult(songMetadata);
            started.TrySetResult();
            return metadata.Task;
        });

        var scan = _libraryService.RescanFolderForMusicAsync(folder.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _libraryService.Dispose();
        metadata.SetResult(songMetadata);

        (await scan.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Songs.CountAsync()).Should().Be(duringReplayGain ? 1 : 0);
    }

    [Fact]
    public async Task RefreshAllFoldersAsync_WhenReplacingAnActiveRefresh_RunsReplayGainOnceAfterAllFolders()
    {
        var firstFolder = new Folder { Path = @"C:\Music\A", Name = "A" };
        var secondFolder = new Folder { Path = @"C:\Music\B", Name = "B" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.AddRange(firstFolder, secondFolder);
            await context.SaveChangesAsync();
        }

        var firstPath = firstFolder.Path + @"\first.mp3";
        var secondPath = secondFolder.Path + @"\second.mp3";
        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(firstFolder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (firstPath, DateTime.UtcNow) });
        _fileSystem.EnumerateFilesWithLastWriteTime(secondFolder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (secondPath, DateTime.UtcNow) });

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstMetadata = new TaskCompletionSource<SongFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementMetadata = new TaskCompletionSource<SongFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractionCount = 0;
        _metadataService.ExtractMetadataAsync(firstPath, firstFolder.Path).Returns(_ =>
        {
            if (Interlocked.Increment(ref extractionCount) == 1)
            {
                firstStarted.SetResult();
                return firstMetadata.Task;
            }
            replacementStarted.SetResult();
            return replacementMetadata.Task;
        });
        _metadataService.ExtractMetadataAsync(secondPath, secondFolder.Path)
            .Returns(new SongFileMetadata { FilePath = secondPath, Title = "Second" });
        _settingsService.GetVolumeNormalizationEnabledAsync().Returns(true);
        var songCountsAtAnalysis = new List<int>();
        _replayGainService.ScanLibraryAsync(Arg.Any<IProgress<ScanProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await using var context = _dbHelper.ContextFactory.CreateDbContext();
                songCountsAtAnalysis.Add(await context.Songs.CountAsync());
            });

        var firstRefresh = _libraryService.RefreshAllFoldersAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacementRefresh = _libraryService.RefreshAllFoldersAsync();
        firstMetadata.SetResult(new SongFileMetadata { FilePath = firstPath, Title = "First" });
        (await firstRefresh.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        replacementMetadata.SetResult(new SongFileMetadata { FilePath = firstPath, Title = "First" });

        (await replacementRefresh.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        songCountsAtAnalysis.Should().Equal(2);
    }

    [Fact]
    public async Task BackgroundMetadataFetch_AttemptsEachArtistOncePerRunAndRetriesOnNextRun()
    {
        _settingsService.GetFetchOnlineMetadataEnabledAsync().Returns(true);
        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting> { new() { Id = ServiceProviderIds.LastFm } });
        _lastFmService.GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromTemporaryError());
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.AddRange(Enumerable.Range(0, 51).Select(i => new Artist { Name = $"Artist {i}" }));
            await context.SaveChangesAsync();
        }

        await _libraryService.StartMetadataBackgroundFetchAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await _lastFmService.Received(51).GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
            (await context.Artists.CountAsync(a => a.MetadataLastCheckedUtc == null)).Should().Be(51);

        _lastFmService.GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccessNotFound());
        await _libraryService.StartMetadataBackgroundFetchAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await _lastFmService.Received(102).GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Artists.CountAsync(a => a.MetadataLastCheckedUtc == null)).Should().Be(0);
    }

    [Fact]
    public async Task ClearAllLibraryDataAsync_WhenCalled_DeletesAllDataAndRecreatesDirectories()
    {
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = new Song
            {
                FilePath = @"C:\Music\song.mp3", Folder = new Folder { Name = "Test", Path = @"C:\Music" },
                Album = new Album { Title = "Album" }, Genres = [new Genre { Name = "Genre" }],
                SongArtists = [new SongArtist { Artist = new Artist { Name = "Test Artist" } }]
            };
            context.ListenHistory.Add(new ListenHistory { Song = song });
            context.PlaylistSongs.Add(new PlaylistSong { Song = song, Playlist = new Playlist { Name = "Playlist" } });
            context.SmartPlaylists.Add(new SmartPlaylist
            {
                Name = "Favorites",
                Rules = [new SmartPlaylistRule { Field = SmartPlaylistField.Title, Operator = SmartPlaylistOperator.Contains, Value = "favorite" }]
            });
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(true);

        await _libraryService.ClearAllLibraryDataAsync();

        await using (var assertContext = _dbHelper.ContextFactory.CreateDbContext())
        {
            (await assertContext.Folders.CountAsync()).Should().Be(0);
            (await assertContext.Artists.CountAsync()).Should().Be(0);
            (await assertContext.Songs.CountAsync()).Should().Be(0);
            (await assertContext.Albums.CountAsync()).Should().Be(0);
            (await assertContext.Genres.CountAsync()).Should().Be(0);
            (await assertContext.Playlists.CountAsync()).Should().Be(0);
            (await assertContext.PlaylistSongs.CountAsync()).Should().Be(0);
            (await assertContext.ListenHistory.CountAsync()).Should().Be(0);
            (await assertContext.SmartPlaylists.CountAsync()).Should().Be(0);
            (await assertContext.SmartPlaylistRules.CountAsync()).Should().Be(0);
        }

        _fileSystem.Received(1).DeleteDirectory(_pathConfig.AlbumArtCachePath, true);
        _fileSystem.Received(1).DeleteDirectory(_pathConfig.ArtistImageCachePath, true);
        _fileSystem.Received(1).DeleteDirectory(_pathConfig.LrcCachePath, true);
        _fileSystem.Received(1).DeleteDirectory(_pathConfig.PlaylistImageCachePath, true);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.AlbumArtCachePath);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.ArtistImageCachePath);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.LrcCachePath);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.PlaylistImageCachePath);
    }

    [Fact]
    public async Task RemoveFolderAsync_CleansEmptyBranchesAndPreservesOccupiedAncestors()
    {
        var removed = new Folder { Name = "Removed", Path = @"D:\Removed" };
        var root = new Folder { Name = "Music", Path = @"C:\Music" };
        var artist = new Folder { Name = "École", Path = @"C:\Music\École", ParentFolder = root };
        var album = new Folder { Name = "Album", Path = @"C:\Music\École\Album", ParentFolder = artist };
        var empty = new Folder { Name = "Empty", Path = @"C:\Music\Empty", ParentFolder = root };
        var leaf = new Folder { Name = "Leaf", Path = @"C:\Music\Empty\Leaf", ParentFolder = empty };
        var similarPrefix = new Folder { Name = "Écolerie", Path = @"C:\Music\Écolerie", ParentFolder = root };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.AddRange(removed, album, leaf, similarPrefix);
            context.Songs.Add(new Song
            {
                Folder = root, FilePath = @"C:\Music\école\Album\song.mp3", DirectoryPath = @"C:\Music\école\Album\"
            });
            context.Albums.Add(new Album { Title = "Empty album" });
            context.Genres.Add(new Genre { Name = "Empty genre" });
            await context.SaveChangesAsync();
        }

        (await _libraryService.RemoveFolderAsync(removed.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.Select(f => f.Id).ToListAsync()).Should().BeEquivalentTo(new[] { root.Id, artist.Id, album.Id });
        (await assertContext.Albums.CountAsync()).Should().Be(0);
        (await assertContext.Genres.CountAsync()).Should().Be(0);
        (await assertContext.Songs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RemoveFolderAsync_PreservesUnrelatedLegacyArtAndLyricsWrites()
    {
        var removed = new Folder { Path = @"D:\Removed" };
        var root = new Folder { Path = @"C:\Music" };
        var art = @"C:\cache\albumart\legacy.fetched.jpg";
        var artistArt = @"C:\cache\artistimages\legacy.jpg";
        var playlistArt = @"C:\cache\playlistimages\legacy.jpg";
        var pendingLyrics = @"C:\cache\lrc\song.lrc.pending.tmp";
        var album = new Album { Title = "Album", CoverArtUri = art };
        var artist = new Artist { Name = "Artist", LocalImageCachePath = artistArt };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(removed);
            context.Songs.Add(new Song
            {
                Folder = root, FilePath = @"C:\Music\song.mp3", DirectoryPath = root.Path,
                Album = album, AlbumArtUriFromTrack = art, SongArtists = [new SongArtist { Artist = artist }]
            });
            context.Playlists.Add(new Playlist { Name = "Playlist", CoverImageUri = playlistArt });
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(true);
        _fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        _fileSystem.EnumerateFiles(_pathConfig.AlbumArtCachePath, "*.fetched.jpg", SearchOption.TopDirectoryOnly).Returns(new[] { art });
        _fileSystem.EnumerateFiles(_pathConfig.ArtistImageCachePath, "*.*", SearchOption.TopDirectoryOnly).Returns(new[] { artistArt });
        _fileSystem.EnumerateFiles(_pathConfig.PlaylistImageCachePath, "*.*", SearchOption.TopDirectoryOnly).Returns(new[] { playlistArt });
        _fileSystem.EnumerateFiles(_pathConfig.LrcCachePath, "*.*", SearchOption.TopDirectoryOnly).Returns(new[] { pendingLyrics });

        (await _libraryService.RemoveFolderAsync(removed.Id)).Should().BeTrue();

        _fileSystem.DidNotReceive().DeleteFile(Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveFolderAsync_CleansLocalArtistImageAfterRemovingOrphan(bool imageLocked)
    {
        var folder = new Folder { Path = @"C:\Music" };
        var artist = new Artist { Name = "Orphan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        var imagePath = Path.Combine(_pathConfig.ArtistImageCachePath, $"{artist.Id}.local.jpg");
        _fileSystem.DirectoryExists(_pathConfig.ArtistImageCachePath).Returns(true);
        _fileSystem.FileExists(imagePath).Returns(true);
        if (imageLocked)
            _fileSystem.When(fs => fs.DeleteFile(imagePath)).Do(_ => throw new IOException("File is locked"));

        (await _libraryService.RemoveFolderAsync(folder.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Artists.CountAsync()).Should().Be(0);
        _fileSystem.Received(1).DeleteFile(imagePath);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_CreatesIntermediateFoldersAndRepairsExistingParent()
    {
        var root = new Folder { Name = "Music", Path = @"C:\Music" };
        var album = new Folder { Name = "Album", Path = @"C:\Music\Artist\Album", ParentFolder = root };
        var filePath = @"C:\Music\Artist\Album\song.mp3";
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(album);
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(root.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(root.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (filePath, DateTime.UtcNow) });
        _metadataService.ExtractMetadataAsync(filePath, root.Path).Returns(new SongFileMetadata
        {
            FilePath = filePath, Title = "Song", Artists = ["Artist"], Album = "Album"
        });

        await _libraryService.RescanFolderForMusicAsync(root.Id);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var artistFolder = await assertContext.Folders.SingleAsync(f => f.Path == @"C:\Music\Artist");
        artistFolder.ParentFolderId.Should().Be(root.Id);
        (await assertContext.Folders.FindAsync(album.Id))!.ParentFolderId.Should().Be(artistFolder.Id);
        (await assertContext.Folders.CountAsync()).Should().Be(3);
    }

    [Theory]
    [InlineData(@"C:\Music\Rock", "Rock")]
    [InlineData(@"C:\Music\Vol. 1", "Vol. 1")]
    [InlineData(@"C:\", @"C:\")]
    public async Task AddFolderAsync_WithValidNewPath_PreservesFolderName(string folderPath, string expectedName)
    {
        var lastWriteTime = DateTime.UtcNow;
        _fileSystem.GetLastWriteTimeUtc(folderPath).Returns(lastWriteTime);

        var result = await _libraryService.AddFolderAsync(folderPath);

        result.Should().NotBeNull();
        result!.Path.Should().Be(folderPath);
        result.Name.Should().Be(expectedName);
        result.LastModifiedDate.Should().Be(lastWriteTime);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AddFolderAsync_WithExistingPath_ReturnsExistingFolderWithoutAdding()
    {
        const string folderPath = "C:\\Music\\Pop";
        var existingFolder = new Folder { Path = folderPath, Name = "Pop" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(existingFolder);
            await context.SaveChangesAsync();
        }

        var result = await _libraryService.AddFolderAsync(folderPath);

        result.Should().NotBeNull();
        result!.Id.Should().Be(existingFolder.Id);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RemoveFolderAsync_WithExistingFolder_RemovesFolderAndAssociatedData()
    {
        var folder = new Folder { Path = "C:\\Music\\Jazz", Name = "Jazz" };
        var artist = new Artist { Name = "Jazz Artist" };
        var album = new Album { Title = "Jazz Album" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = artist, Order = 0 });
        var song = new Song
        {
            FilePath = "C:\\Music\\Jazz\\track1.mp3",
            Title = "Jazz Track",
            Folder = folder,
            Album = album,
            AlbumArtUriFromTrack = "C:\\cache\\albumart\\art1.jpg",
            LrcFilePath = "C:\\cache\\lrc\\lyrics1.lrc"
        };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        album.SyncDenormalizedFields();
        song.SyncDenormalizedFields();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.FileExists("C:\\cache\\albumart\\art1.jpg").Returns(true);
        _fileSystem.FileExists("C:\\cache\\lrc\\lyrics1.lrc").Returns(true);
        _pathConfig.LrcCachePath.Returns("C:\\cache\\lrc");

        var result = await _libraryService.RemoveFolderAsync(folder.Id);

        result.Should().BeTrue();

        await using (var assertContext = _dbHelper.ContextFactory.CreateDbContext())
        {
            (await assertContext.Folders.CountAsync()).Should().Be(0);
            (await assertContext.Songs.CountAsync()).Should().Be(0);
            (await assertContext.Albums.CountAsync()).Should().Be(0);
            (await assertContext.Artists.CountAsync()).Should().Be(0);
        }

        _fileSystem.Received(1).DeleteFile("C:\\cache\\albumart\\art1.jpg");
        _fileSystem.Received(1).DeleteFile("C:\\cache\\lrc\\lyrics1.lrc");
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithMixedChanges_CorrectlyUpdatesLibrary()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Scan", Name = "Scan" };
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist" };
        var existingSongUnchanged = new Song
        {
            FilePath = "C:\\Music\\Scan\\unchanged.mp3",
            FileModifiedDate = new DateTime(2023, 1, 1),
            FolderId = folder.Id
        };
        existingSongUnchanged.SongArtists.Add(new SongArtist { ArtistId = artist.Id, Order = 0 });
        var existingSongToUpdate = new Song
        {
            FilePath = "C:\\Music\\Scan\\updated.mp3",
            FileModifiedDate = new DateTime(2023, 1, 1),
            FolderId = folder.Id
        };
        existingSongToUpdate.SongArtists.Add(new SongArtist { ArtistId = artist.Id, Order = 0 });
        var existingSongToDelete = new Song
        {
            FilePath = "C:\\Music\\Scan\\deleted.mp3",
            FileModifiedDate = new DateTime(2023, 1, 1),
            FolderId = folder.Id
        };
        existingSongToDelete.SongArtists.Add(new SongArtist { ArtistId = artist.Id, Order = 0 });
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(existingSongUnchanged, existingSongToUpdate, existingSongToDelete);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories).Returns(new[]
        {
            ("C:\\Music\\Scan\\unchanged.mp3", new DateTime(2023, 1, 1)),
            ("C:\\Music\\Scan\\updated.mp3",   new DateTime(2023, 2, 2)),
            ("C:\\Music\\Scan\\new.mp3",       new DateTime(2023, 3, 3)),
        });

        _metadataService.ExtractMetadataAsync("C:\\Music\\Scan\\updated.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
            { FilePath = "C:\\Music\\Scan\\updated.mp3", Title = "Updated Song", Artists = new List<string> { "Artist" } });
        _metadataService.ExtractMetadataAsync("C:\\Music\\Scan\\new.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
            { FilePath = "C:\\Music\\Scan\\new.mp3", Title = "New Song", Artists = new List<string> { "Artist" } });

        var result = await _libraryService.RescanFolderForMusicAsync(folder.Id);

        result.Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var songs = await assertContext.Songs.ToListAsync();
        songs.Should().HaveCount(3);
        songs.Should().Contain(s => s.FilePath == "C:\\Music\\Scan\\unchanged.mp3");
        songs.Should().Contain(s => s.FilePath == "C:\\Music\\Scan\\updated.mp3" && s.Title == "Updated Song");
        songs.Should().Contain(s => s.FilePath == "C:\\Music\\Scan\\new.mp3" && s.Title == "New Song");
        songs.Should().NotContain(s => s.FilePath == "C:\\Music\\Scan\\deleted.mp3");
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WhenFolderPathNoLongerExists_RemovesFolderFromLibrary()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\DeletedFolder", Name = "Deleted" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(false);

        var result = await _libraryService.RescanFolderForMusicAsync(folder.Id);

        result.Should().BeTrue();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WhenCancelled_StopsProcessingAndReturnsFalse()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Scan", Name = "Scan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { ("C:\\Music\\Scan\\file1.mp3", DateTime.UtcNow) });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _libraryService.RescanFolderForMusicAsync(folder.Id, cancellationToken: cts.Token);

        result.Should().BeFalse();
        await _metadataService.DidNotReceive().ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithNewTxtLyricsFile_UpdatesSongLrcFilePath()
    {
        var lastWriteTime = DateTime.UtcNow;
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Scan", Name = "Scan" };
        var song = new Song
        {
            Id = Guid.NewGuid(),
            FilePath = "C:\\Music\\Scan\\song.mp3",
            FolderId = folder.Id,
            FileModifiedDate = lastWriteTime
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (song.FilePath, lastWriteTime) });

        var lyricsPath = "C:\\Music\\Scan\\song.txt";
        _fileSystem.GetFiles("C:\\Music\\Scan", "*.lrc").Returns(Array.Empty<string>());
        _fileSystem.GetFiles("C:\\Music\\Scan", "*.txt").Returns(new[] { lyricsPath });

        var result = await _libraryService.RescanFolderForMusicAsync(folder.Id);

        result.Should().BeTrue();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updatedSong = await assertContext.Songs.FindAsync(song.Id);
        updatedSong.Should().NotBeNull();
        updatedSong!.LrcFilePath.Should().Be(lyricsPath);
    }

    [Fact]
    public async Task GetArtistDetailsAsync_WithMissingMetadataAndFetchAllowed_FetchesAndUpdatesArtist()
    {
        var artist = new Artist
        { Id = Guid.NewGuid(), Name = "Remote Artist", Biography = null, LocalImageCachePath = null };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }

        _settingsService.GetEnabledServiceProvidersAsync(ServiceCategory.Metadata)
            .Returns(new List<ServiceProviderSetting>
            {
                new() { Id = ServiceProviderIds.LastFm, IsEnabled = true, Category = ServiceCategory.Metadata }
            });

        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo
            { Biography = "A cool bio.", ImageUrl = "http://example.com/image.jpg" }));

        var artistCachePath = _pathConfig.ArtistImageCachePath;
        var artistImageFilename = $"{artist.Id}.fetched.jpg";
        var expectedImagePath = Path.Combine(artistCachePath, artistImageFilename);
        _fileSystem.FileExists(expectedImagePath).Returns(false);
        _imageProcessor.ProcessImageBytesAsync(Arg.Any<byte[]>()).Returns(new byte[] { 4, 5, 6 });

        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        });

        var eventFired = false;
        _libraryService.ArtistMetadataUpdated += (sender, args) =>
        {
            eventFired = true;
            args.ArtistId.Should().Be(artist.Id);
            args.NewLocalImageCachePath.Should().Be(expectedImagePath);
        };

        var result = await _libraryService.GetArtistDetailsAsync(artist.Id, true);

        result.Should().NotBeNull();
        result!.Biography.Should().Be("A cool bio.");
        result.LocalImageCachePath.Should().Be(expectedImagePath);
        result.MetadataLastCheckedUtc.Should().NotBeNull();
        eventFired.Should().BeTrue();

        _fileSystem.Received(1).MoveFile(Arg.Is<string>(path => path.EndsWith(".tmp")), expectedImagePath, true);
    }

    [Fact]
    public async Task GetArtistDetailsAsync_WithFetchDisallowed_DoesNotCallRemoteServices()
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Local Artist", Biography = null };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }

        var result = await _libraryService.GetArtistDetailsAsync(artist.Id, false);

        result.Should().NotBeNull();
        result!.Biography.Should().BeNull();

        await _lastFmService.DidNotReceive().GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSongLrcPathAsync_PreservesArtistName()
    {
        Guid songId;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var folder = new Folder { Path = "C:\\Music", Name = "Music" };
            context.Folders.Add(folder);

            var artist = new Artist { Name = "Preserved Artist" };
            var song = new Song
            {
                Title = "Original Title",
                Folder = folder,
                FilePath = "C:\\Music\\preserve.mp3",
                DirectoryPath = "C:\\Music"
            };
            song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
            context.Artists.Add(artist);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
            songId = song.Id;

            song.ArtistName.Should().Be("Preserved Artist");
        }

        await _libraryService.UpdateSongLrcPathAsync(songId, @"C:\cache\lrc\preserve.lrc");

        await using (var assertContext = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = await assertContext.Songs.FirstAsync(s => s.Id == songId);
            song.LrcFilePath.Should().Be(@"C:\cache\lrc\preserve.lrc");
            song.ArtistName.Should().Be("Preserved Artist");
            song.PrimaryArtistName.Should().Be("Preserved Artist");
        }
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_SupersedingScansWaitForActiveWorkAndApplyOnlyLatest()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Concurrent", Name = "Concurrent" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { ("C:\\Music\\Concurrent\\song1.mp3", DateTime.UtcNow) });

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<SongFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(_ =>
        {
            started.TrySetResult();
            return metadata.Task;
        });

        var first = _libraryService.RescanFolderForMusicAsync(folder.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = _libraryService.RescanFolderForMusicAsync(folder.Id);
        var third = _libraryService.RescanFolderForMusicAsync(folder.Id);
        try
        {
            third.IsCompleted.Should().BeFalse();
        }
        finally
        {
            metadata.TrySetResult(new SongFileMetadata
            {
                FilePath = "C:\\Music\\Concurrent\\song1.mp3",
                Title = "Concurrent Song",
                Artists = new List<string> { "Artist" }
            });
        }

        (await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Equal(false, false, true);
        await _metadataService.Received(2).ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>());

        await using (var assertContext = _dbHelper.ContextFactory.CreateDbContext())
        {
            var songs = await assertContext.Songs.Where(s => s.FolderId == folder.Id).ToListAsync();

            songs.Should().HaveCount(1);
            songs[0].Title.Should().Be("Concurrent Song");
        }
    }

    [Fact]
    public async Task StartListenSessionAsync_CreatesHistoryEntry_WithCorrectContext()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Test" };
        var song = new Song { Title = "Test Song", Folder = folder, FilePath = "C:\\Test\\song.mp3" };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        var playbackContext = new PlaybackContext(PlaybackContextType.Album, Guid.NewGuid());

        var sessionId = await _libraryService.StartListenSessionAsync(song.Id, playbackContext);

        sessionId.Should().NotBeNull();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var historyEntry = await assertContext.ListenHistory.FindAsync(sessionId);

        historyEntry.Should().NotBeNull();
        historyEntry!.SongId.Should().Be(song.Id);
        historyEntry.ContextType.Should().Be(PlaybackContextType.Album);
        historyEntry.ContextId.Should().Be(playbackContext.ContextId);
        historyEntry.ListenDurationTicks.Should().Be(0);
        historyEntry.EndReason.Should().Be(PlaybackEndReason.PausedAndAbandoned);
        historyEntry.IsEligibleForScrobbling.Should().BeFalse();
    }

    [Fact]
    public async Task StartListenSessionAsync_WithInvalidSongId_ReturnsNull()
    {
        var sessionId = await _libraryService.StartListenSessionAsync(Guid.NewGuid(), new PlaybackContext(PlaybackContextType.Library, null));

        sessionId.Should().BeNull();
    }

    [Fact]
    public async Task MarkListenAsEligibleForScrobblingAsync_IncrementsPlayCountAndSetsLastPlayedDate()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Test" };
        var song = new Song { Title = "Test Song", Folder = folder, FilePath = "C:\\Test\\song.mp3", PlayCount = 5 };
        var listenHistory = new ListenHistory { Song = song, IsEligibleForScrobbling = false };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            context.ListenHistory.Add(listenHistory);
            await context.SaveChangesAsync();
        }

        var beforeUpdate = DateTime.UtcNow;

        var result = await _libraryService.MarkListenAsEligibleForScrobblingAsync(listenHistory.Id);

        result.Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updatedHistory = await assertContext.ListenHistory.FindAsync(listenHistory.Id);
        var updatedSong = await assertContext.Songs.FindAsync(song.Id);

        updatedHistory!.IsEligibleForScrobbling.Should().BeTrue();
        updatedSong!.PlayCount.Should().Be(6);
        updatedSong.LastPlayedDate.Should().BeOnOrAfter(beforeUpdate);
    }

    [Fact]
    public async Task FinalizeListenSessionAsync_WhenCalled_UpdatesDurationAndEndReason()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Test" };
        var song = new Song { Title = "Test Song", Folder = folder, FilePath = "C:\\Test\\song.mp3", TotalListenTimeTicks = 1000 };
        var listenHistory = new ListenHistory { Song = song, EndReason = PlaybackEndReason.PausedAndAbandoned, ListenDurationTicks = 0, IsEligibleForScrobbling = true };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            context.ListenHistory.Add(listenHistory);
            await context.SaveChangesAsync();
        }

        var finalDuration = TimeSpan.FromSeconds(150);

        await _libraryService.FinalizeListenSessionAsync(listenHistory.Id, finalDuration, PlaybackEndReason.Finished);
        await _libraryService.FinalizeListenSessionAsync(listenHistory.Id, TimeSpan.FromSeconds(5), PlaybackEndReason.Skipped);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updatedHistory = await assertContext.ListenHistory.FindAsync(listenHistory.Id);
        var updatedSong = await assertContext.Songs.FindAsync(song.Id);

        updatedHistory!.EndReason.Should().Be(PlaybackEndReason.Finished);
        updatedHistory.ListenDurationTicks.Should().Be(finalDuration.Ticks);

        updatedSong!.TotalListenTimeTicks.Should().Be(1000 + finalDuration.Ticks);
    }

    [Fact]
    public async Task FinalizeListenSessionAsync_WhenSkippedAndNotEligible_IncrementsSkipCount()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Test" };
        var song = new Song { Title = "Test Song", Folder = folder, FilePath = "C:\\Test\\song.mp3", SkipCount = 2, TotalListenTimeTicks = 0 };

        var listenHistory = new ListenHistory { Song = song, EndReason = PlaybackEndReason.PausedAndAbandoned, ListenDurationTicks = 0, IsEligibleForScrobbling = false };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            context.ListenHistory.Add(listenHistory);
            await context.SaveChangesAsync();
        }

        var finalDuration = TimeSpan.FromSeconds(10);

        await _libraryService.FinalizeListenSessionAsync(listenHistory.Id, finalDuration, PlaybackEndReason.Skipped);
        await _libraryService.FinalizeListenSessionAsync(listenHistory.Id, finalDuration, PlaybackEndReason.Skipped);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updatedHistory = await assertContext.ListenHistory.FindAsync(listenHistory.Id);
        var updatedSong = await assertContext.Songs.FindAsync(song.Id);

        updatedHistory!.EndReason.Should().Be(PlaybackEndReason.Skipped);
        updatedHistory.ListenDurationTicks.Should().Be(finalDuration.Ticks);

        updatedSong!.SkipCount.Should().Be(3);
        updatedSong.TotalListenTimeTicks.Should().Be(finalDuration.Ticks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ListenCallbacks_PreserveCountsRegardlessOfEligibilityOrder(bool eligibilityFirst)
    {
        var song = new Song
        {
            Folder = new Folder { Path = @"C:\Music", Name = "Music" },
            FilePath = @"C:\Music\song.mp3",
            PlayCount = 5,
            SkipCount = 2
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        var id = (await _libraryService.StartListenSessionAsync(song.Id, new PlaybackContext(PlaybackContextType.Library, null)))!.Value;
        if (eligibilityFirst)
            await _libraryService.MarkListenAsEligibleForScrobblingAsync(id);
        await _libraryService.FinalizeListenSessionAsync(id, TimeSpan.FromMinutes(3), PlaybackEndReason.Skipped);
        await _libraryService.MarkListenAsEligibleForScrobblingAsync(id);
        await _libraryService.MarkListenAsEligibleForScrobblingAsync(id);
        await _libraryService.FinalizeListenSessionAsync(id, TimeSpan.FromMinutes(3), PlaybackEndReason.Skipped);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updatedSong = await assertContext.Songs.SingleAsync();
        updatedSong.PlayCount.Should().Be(6);
        updatedSong.SkipCount.Should().Be(2);
        updatedSong.TotalListenTimeTicks.Should().Be(TimeSpan.FromMinutes(3).Ticks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeduplicateLibraryAsync_PreservesUserDataAndRollsBackFailedMerges(bool failDuringSongMerge)
    {
        const string alias = @"Z:\Music";
        const string canonical = @"\\server\share\Music";
        var aliasRoot = new Folder { Path = alias, Name = "Music", LastModifiedDate = DateTime.UtcNow.AddDays(-2) };
        var root = new Folder { Path = canonical, Name = "Music", LastModifiedDate = DateTime.UtcNow };
        var aliasChild = new Folder { Path = alias + @"\Album", Name = "Album", ParentFolder = aliasRoot, LastModifiedDate = aliasRoot.LastModifiedDate };
        var child = new Folder { Path = canonical + @"\Album", Name = "Album", ParentFolder = root, LastModifiedDate = root.LastModifiedDate };
        var first = new Song { FilePath = aliasChild.Path + @"\song.mp3", DirectoryPath = aliasChild.Path, Folder = aliasChild,
            PlayCount = 5, SkipCount = 2, TotalListenTimeTicks = 100, DateAddedToLibrary = DateTime.UtcNow.AddDays(-10) };
        var second = new Song { FilePath = child.Path + @"\song.mp3", DirectoryPath = child.Path, Folder = child,
            PlayCount = 3, SkipCount = 1, TotalListenTimeTicks = 50, IsLoved = true, Rating = 4, LastPlayedDate = DateTime.UtcNow };
        var shared = new Playlist { Name = "Shared" };
        var duplicateOnly = new Playlist { Name = "Duplicate only" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(first, second);
            context.ListenHistory.AddRange(new ListenHistory { Song = first },
                new ListenHistory { Song = second, ContextType = PlaybackContextType.Folder, ContextId = child.Id });
            context.PlaylistSongs.AddRange(
                new PlaylistSong { Playlist = shared, Song = first, Order = 5 },
                new PlaylistSong { Playlist = shared, Song = second, Order = 2 },
                new PlaylistSong { Playlist = duplicateOnly, Song = second, Order = 7 });
            await context.SaveChangesAsync();
        }
        _fileSystem.NormalizePath(Arg.Any<string>()).Returns(call =>
        {
            var path = call.Arg<string>();
            if (failDuringSongMerge && path.EndsWith(".mp3")) throw new IOException("Normalization failed");
            return path.Replace(alias, canonical, StringComparison.OrdinalIgnoreCase);
        });
        var notifications = 0;
        _libraryService.LibraryContentChanged += (_, _) => notifications++;

        if (failDuringSongMerge)
        {
            Func<Task> merge = () => _libraryService.DeduplicateLibraryAsync();
            await merge.Should().ThrowAsync<IOException>();
            await using var context = _dbHelper.ContextFactory.CreateDbContext();
            (await context.Folders.CountAsync()).Should().Be(4);
            (await context.Songs.CountAsync()).Should().Be(2);
            (await context.PlaylistSongs.CountAsync()).Should().Be(3);
            (await context.Songs.FindAsync(second.Id))!.FolderId.Should().Be(child.Id);
            notifications.Should().Be(0);
            failDuringSongMerge = false;
        }

        (await _libraryService.DeduplicateLibraryAsync()).Should().Be(3);
        (await _libraryService.DeduplicateLibraryAsync()).Should().Be(0);
        notifications.Should().Be(1);
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var survivor = await assertContext.Songs.SingleAsync();
        survivor.Id.Should().Be(first.Id);
        survivor.FilePath.Should().Be(second.FilePath);
        survivor.DirectoryPath.Should().Be(child.Path);
        survivor.FolderId.Should().Be(aliasChild.Id);
        survivor.PlayCount.Should().Be(8);
        survivor.SkipCount.Should().Be(3);
        survivor.TotalListenTimeTicks.Should().Be(150);
        survivor.IsLoved.Should().BeTrue();
        survivor.Rating.Should().Be(4);
        survivor.DateAddedToLibrary.Should().Be(first.DateAddedToLibrary);
        survivor.LastPlayedDate.Should().Be(second.LastPlayedDate);
        (await assertContext.Folders.CountAsync()).Should().Be(2);
        (await assertContext.Folders.FindAsync(aliasChild.Id))!.ParentFolderId.Should().Be(aliasRoot.Id);
        (await assertContext.ListenHistory.Select(history => history.SongId).ToListAsync()).Should().Equal(first.Id, first.Id);
        (await assertContext.ListenHistory.SingleAsync(history => history.ContextType == PlaybackContextType.Folder))
            .ContextId.Should().Be(aliasChild.Id);
        var entries = await assertContext.PlaylistSongs.ToListAsync();
        entries.Should().HaveCount(2).And.OnlyContain(entry => entry.SongId == first.Id);
        entries.Single(entry => entry.PlaylistId == shared.Id).Order.Should().Be(2);
        entries.Single(entry => entry.PlaylistId == duplicateOnly.Id).Order.Should().Be(7);
    }

    [Fact]
    public async Task ListenCallbacks_ConcurrentSessionsPreserveCounters()
    {
        using var database = new DbContextFactoryTestHelper(useFileDatabase: true);
        using var reader = new LibraryReader(database.ContextFactory, _fileSystem, _settingsService,
            Substitute.For<ILogger<LibraryReader>>());
        using var service = new LibraryService(database.ContextFactory, reader, _fileSystem, _metadataService,
            new ArtistMetadataService(_lastFmService, _musicBrainzService, _fanartTvService, _theAudioDbService,
                Substitute.For<IDeezerService>(), Substitute.For<IWikipediaService>(), _artistMetadataLogger),
            Substitute.For<IAlbumCoverService>(), _httpClientFactory, _pathConfig, _settingsService, _replayGainService, _imageProcessor, _pipelines, _logger);
        var song = new Song { FilePath = @"C:\Music\song.mp3", Folder = new Folder { Path = @"C:\Music", Name = "Music" } };
        await using (var context = database.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }
        var playbackContext = new PlaybackContext(PlaybackContextType.Library, null);
        var first = (await service.StartListenSessionAsync(song.Id, playbackContext))!.Value;
        var second = (await service.StartListenSessionAsync(song.Id, playbackContext))!.Value;

        await Task.WhenAll(new[] { first, second }.SelectMany(id => new Func<Task>[]
        {
            () => service.MarkListenAsEligibleForScrobblingAsync(id),
            () => service.MarkListenAsEligibleForScrobblingAsync(id),
            () => service.FinalizeListenSessionAsync(id, TimeSpan.FromSeconds(40), PlaybackEndReason.Skipped),
            () => service.FinalizeListenSessionAsync(id, TimeSpan.FromSeconds(40), PlaybackEndReason.Skipped)
        }).Select(callback => Task.Run(callback)));

        await using var assertContext = database.ContextFactory.CreateDbContext();
        var updated = await assertContext.Songs.SingleAsync();
        updated.PlayCount.Should().Be(2);
        updated.SkipCount.Should().Be(0);
        updated.TotalListenTimeTicks.Should().Be(TimeSpan.FromSeconds(80).Ticks);
        (await assertContext.ListenHistory.CountAsync(history => history.IsEligibleForScrobbling)).Should().Be(2);
    }

    [Fact]
    public async Task ListenCallbacks_ForMissingHistory_DoNotRecreateEntries()
    {
        await _libraryService.FinalizeListenSessionAsync(99999L, TimeSpan.FromSeconds(10), PlaybackEndReason.Finished);
        (await _libraryService.MarkListenAsEligibleForScrobblingAsync(99999L)).Should().BeFalse();
        (await _libraryService.MarkListenAsSubmittedToListenBrainzAsync(99999L)).Should().BeFalse();
        (await _libraryService.MarkListenAsScrobbledAsync(99999L)).Should().BeFalse();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.ListenHistory.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ScrobbleAcknowledgements_PersistBothServicesAndAreIdempotent()
    {
        var folder = new Folder { Name = "Test Folder", Path = "C:\\Test" };
        var song = new Song { Title = "Test Song", Folder = folder, FilePath = "C:\\Test\\song.mp3" };
        var listenHistory = new ListenHistory
        {
            Song = song,
            IsEligibleForScrobbling = true,
            IsSubmittedToListenBrainz = false
        };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            context.ListenHistory.Add(listenHistory);
            await context.SaveChangesAsync();
        }

        var result = await _libraryService.MarkListenAsSubmittedToListenBrainzAsync(listenHistory.Id);
        (await _libraryService.MarkListenAsSubmittedToListenBrainzAsync(listenHistory.Id)).Should().BeTrue();
        (await _libraryService.MarkListenAsScrobbledAsync(listenHistory.Id)).Should().BeTrue();
        (await _libraryService.MarkListenAsScrobbledAsync(listenHistory.Id)).Should().BeTrue();

        result.Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updatedHistory = await assertContext.ListenHistory.FindAsync(listenHistory.Id);
        updatedHistory!.IsSubmittedToListenBrainz.Should().BeTrue();
        updatedHistory.IsScrobbled.Should().BeTrue();
    }

    [Fact]
    public async Task ClearListenHistoryAsync_DeletesHistoryWithoutRemovingSongs()
    {
        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var song = new Song { Title = "S", Folder = folder, FilePath = "C:\\Music\\s.mp3" };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Songs.Add(song);
            ctx.ListenHistory.AddRange(
                new ListenHistory { Song = song },
                new ListenHistory { Song = song }
            );
            await ctx.SaveChangesAsync();
        }

        await _libraryService.ClearListenHistoryAsync();

        await using var assertCtx = _dbHelper.ContextFactory.CreateDbContext();
        (await assertCtx.ListenHistory.CountAsync()).Should().Be(0, "history should be cleared");
        (await assertCtx.Songs.CountAsync()).Should().Be(1, "songs should not be deleted");
    }

    [Theory]
    [InlineData(@"C:\Users\Music\song.lrc")]
    [InlineData(@"C:\cache\lrc-backup\song.lrc")]
    [InlineData(@"C:\cache\lrc\..\song.lrc")]
    public async Task RemoveFolderAsync_WhenSongHasLrcFileOutsideCache_DoesNotDeleteLrcFile(string userLrcPath)
    {
        _fileSystem.FileExists(userLrcPath).Returns(true);

        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var song = new Song { Title = "S", Folder = folder, FilePath = "C:\\Music\\s.mp3", LrcFilePath = userLrcPath };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Songs.Add(song);
            await ctx.SaveChangesAsync();
        }

        await _libraryService.RemoveFolderAsync(folder.Id);

        _fileSystem.DidNotReceive().DeleteFile(userLrcPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovingLibrarySongs_PreservesSharedAndExternalCoverArt(bool removeFolder)
    {
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        var otherFolder = new Folder { Path = @"C:\Other", Name = "Other" };
        var sharedSongArt = @"C:\cache\albumart\shared-song.jpg";
        var sharedAlbumArt = @"C:\cache\albumart\shared-album.jpg";
        var orphanArt = @"C:\cache\albumart\orphan.jpg";
        var externalArt = @"C:\Music\cover.jpg";
        var remainingAlbum = new Album { Title = "Remaining", CoverArtUri = sharedAlbumArt };
        var removedAlbum = new Album { Title = "Removed", CoverArtUri = orphanArt };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            foreach (var art in new[] { sharedSongArt, sharedAlbumArt, orphanArt, externalArt })
                context.Songs.Add(new Song
                {
                    Folder = folder, FilePath = Path.Combine(folder.Path, Path.GetFileName(art) + ".mp3"),
                    DirectoryPath = folder.Path, AlbumArtUriFromTrack = art, Album = removedAlbum
                });
            context.Songs.Add(new Song
            {
                Folder = otherFolder, FilePath = @"C:\Other\remaining.mp3", DirectoryPath = otherFolder.Path,
                Album = remainingAlbum, AlbumArtUriFromTrack = sharedSongArt
            });
            await context.SaveChangesAsync();
        }
        _fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(Array.Empty<(string, DateTime)>());

        var changed = removeFolder
            ? await _libraryService.RemoveFolderAsync(folder.Id)
            : await _libraryService.RescanFolderForMusicAsync(folder.Id);

        changed.Should().BeTrue();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Songs.CountAsync()).Should().Be(1);
        _fileSystem.Received(1).DeleteFile(orphanArt);
        _fileSystem.DidNotReceive().DeleteFile(sharedSongArt);
        _fileSystem.DidNotReceive().DeleteFile(sharedAlbumArt);
        _fileSystem.DidNotReceive().DeleteFile(externalArt);
    }

    [Fact]
    public async Task Rescan_GivesTracksWithoutArtTheirAlbumCoverExceptInUnknownAlbum()
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        const string albumCover = @"C:\cache\albumart\hash.aaaaaa.bbbbbb.fetched.jpg";
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        var album = new Album { Title = "Album", CoverArtUri = albumCover };
        var unknownAlbum = new Album { Title = Album.UnknownAlbumName, CoverArtUri = albumCover };
        Song Track(string name, Album trackAlbum, string? art = null) => new()
        {
            Folder = folder,
            FilePath = $@"C:\Music\{name}.mp3",
            DirectoryPath = folder.Path,
            FileModifiedDate = timestamp,
            Album = trackAlbum,
            AlbumArtUriFromTrack = art,
            LightSwatchId = art is null ? null : "aaaaaa"
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(Track("with-art", album, albumCover), Track("without-art", album), Track("unknown", unknownAlbum));
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories).Returns(new[]
        {
            (@"C:\Music\with-art.mp3", timestamp), (@"C:\Music\without-art.mp3", timestamp), (@"C:\Music\unknown.mp3", timestamp)
        });

        (await _libraryService.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var songs = await assertContext.Songs.ToDictionaryAsync(s => Path.GetFileNameWithoutExtension(s.FilePath));
        songs["without-art"].AlbumArtUriFromTrack.Should().Be(albumCover);
        songs["without-art"].LightSwatchId.Should().Be("aaaaaa");
        songs["unknown"].AlbumArtUriFromTrack.Should().BeNull();
        await _imageProcessor.DidNotReceive().SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>());
    }

    [Fact]
    public async Task Rescan_KeepsAlbumCoversInStepWithTheirTracks()
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        const string oldCover = @"C:\cache\albumart\old.jpg";
        const string newCover = @"C:\cache\albumart\new.jpg";
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        var artist = new Artist { Name = "Artist" };
        var albums = new Dictionary<string, Album>();
        Song Track(string albumTitle, int trackNumber, string? albumCover = oldCover, string? art = oldCover)
        {
            if (!albums.TryGetValue(albumTitle, out var album))
            {
                album = new Album { Title = albumTitle, CoverArtUri = albumCover };
                album.AlbumArtists.Add(new AlbumArtist { Artist = artist, Order = 0 });
                albums.Add(albumTitle, album);
            }
            var song = new Song
            {
                Folder = folder,
                FilePath = $@"C:\Music\{albumTitle}{trackNumber}.mp3",
                DirectoryPath = folder.Path,
                FileModifiedDate = timestamp,
                Album = album,
                TrackNumber = trackNumber,
                AlbumArtUriFromTrack = art
            };
            song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
            return song;
        }
        var retagged = new[] { Track("Retagged", 1), Track("Retagged", 2) };
        var bonus = new[] { Track("Bonus", 1), Track("Bonus", 2) };
        // Tracks without art of their own show the album's online cover.
        var online = new[] { Track("Online", 1), Track("Online", 2) };
        var uncovered = Track("Uncovered", 1, albumCover: null, art: newCover);
        var changed = retagged.Append(bonus[1]).Concat(online).ToArray();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(retagged.Concat(bonus).Concat(online).Append(uncovered));
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(retagged.Concat(bonus).Concat(online).Append(uncovered)
                .Select(s => (s.FilePath, changed.Contains(s) ? timestamp.AddDays(1) : timestamp)).ToArray());
        foreach (var song in changed)
            _metadataService.ExtractMetadataAsync(song.FilePath, folder.Path, true).Returns(new SongFileMetadata
            {
                FilePath = song.FilePath,
                FileModifiedDate = timestamp.AddDays(1),
                Title = song.FilePath,
                Album = song.Album!.Title,
                TrackNumber = song.TrackNumber,
                Artists = ["Artist"],
                AlbumArtists = ["Artist"],
                CoverArtUri = online.Contains(song) ? null : newCover
            });

        (await _libraryService.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var covers = await assertContext.Albums.ToDictionaryAsync(a => a.Title, a => a.CoverArtUri);
        covers["Retagged"].Should().Be(newCover);
        covers["Bonus"].Should().Be(oldCover, because: "another track still uses the album's cover");
        covers["Online"].Should().Be(oldCover, because: "re-reading tracks without art doesn't make the cover stale");
        covers["Uncovered"].Should().Be(newCover);
        (await assertContext.Songs.Where(s => s.Album!.Title == "Online").Select(s => s.AlbumArtUriFromTrack).ToListAsync())
            .Should().AllBe(oldCover);
    }
}
