using System.Net;
using System.Web;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.OnlineProviders;
using Nagi.Core.OnlineProviders.Pipelines;
using Nagi.Core.Platform;
using Nagi.Core.Scrobbling;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class LastFmScrobblerServiceTests : IDisposable
{
    private const string ApiKey = "test-api-key";
    private const string ApiSecret = "test-api-secret";
    private const string SessionKey = "test-session-key";
    private const string SuccessfulResponse = "<lfm status='ok'><scrobbles accepted='1' ignored='0'><scrobble><ignoredMessage code='0'/></scrobble></scrobbles></lfm>";
    private const string NowPlayingResponse = "<lfm status='ok'><nowplaying><ignoredMessage code='0'/></nowplaying></lfm>";
    private readonly IApiKeyService _apiKeyService;
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILogger<LastFmScrobblerService> _logger;
    private readonly LastFmScrobblerService _scrobblerService;
    private readonly ICredentialLockerService _credentials;
    private readonly SettingsService _settingsService;

    private Dictionary<string, string>? _capturedRequestParams;

    private readonly ProviderPipelineProvider _pipelines;

    public LastFmScrobblerServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _credentials = Substitute.For<ICredentialLockerService>();
        _settingsService = TestSettings.Create(credentials: _credentials);
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<LastFmScrobblerService>>();
        _dbHelper = new DbContextFactoryTestHelper();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.LastFm);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);
        _settingsService.Set(CoreSettings.LastFmScrobbling, true);
        _scrobblerService = CreateService();
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpMessageHandler.Dispose();
        _dbHelper.Dispose();
    }

    private LastFmScrobblerService CreateService()
    {
        return new LastFmScrobblerService(
            _httpClientFactory,
            _pipelines,
            _apiKeyService,
            _settingsService,
            _dbHelper.ContextFactory,
            _logger);
    }
    [Fact]
    public async Task UpdateNowPlayingAsync_WithValidSongAndCredentials_SendsCorrectRequest()
    {
        var song = CreateTestSong();
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK);

        var expectedParams = new Dictionary<string, string>
        {
            { "method", "track.updateNowPlaying" },
            { "artist", song.ArtistName },
            { "track", song.Title },
            { "album", song.Album?.Title ?? string.Empty },
            { "duration", "180" },
            { "api_key", ApiKey },
            { "sk", SessionKey }
        };
        const string expectedSignature = "4a7b67e97bde9cb31f13bfe82c6f27c8";
        expectedParams.Add("api_sig", expectedSignature);

        await _scrobblerService.UpdateNowPlayingAsync(song);

        _httpMessageHandler.Requests.Should().HaveCount(1);
        _httpMessageHandler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _capturedRequestParams.Should().NotBeNull();
        _capturedRequestParams.Should().BeEquivalentTo(expectedParams);
    }
    [Fact]
    public async Task SubmitPendingAsync_WithValidSongAndCredentials_SendsCorrectRequest()
    {
        var song = CreateTestSong();
        var startTime = new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var expectedTimestamp = new DateTimeOffset(startTime).ToUnixTimeSeconds().ToString();
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK);

        var expectedParams = new Dictionary<string, string>
        {
            { "method", "track.scrobble" },
            { "artist", song.ArtistName },
            { "track", song.Title },
            { "album", song.Album?.Title ?? string.Empty },
            { "duration", "180" },
            { "timestamp", expectedTimestamp },
            { "api_key", ApiKey },
            { "sk", SessionKey }
        };
        const string expectedSignature = "43a14dbd8f9f65f1f1929600d9c9d770";
        expectedParams.Add("api_sig", expectedSignature);

        await ScrobbleAsync(song, startTime);

        _httpMessageHandler.Requests.Should().HaveCount(1);
        _httpMessageHandler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _capturedRequestParams.Should().NotBeNull();
        _capturedRequestParams.Should().BeEquivalentTo(expectedParams);
    }
    [Theory]
    [InlineData(null, ApiSecret, SessionKey)]
    [InlineData(ApiKey, null, SessionKey)]
    [InlineData(ApiKey, ApiSecret, null)]
    public async Task ApiCall_WhenCredentialsAreMissing_MakesNoHttpCallAndKeepsTheListen(string? apiKey,
        string? apiSecret, string? sessionKey)
    {
        _apiKeyService.GetApiKeyAsync("lastfm").Returns(Task.FromResult(apiKey));
        _apiKeyService.GetApiKeyAsync("lastfm-secret").Returns(Task.FromResult(apiSecret));
        (string?, string?)? credentials = sessionKey != null ? ("user", sessionKey) : null;
        _credentials.RetrieveCredential("Nagi/LastFm").Returns(credentials);

        await _scrobblerService.UpdateNowPlayingAsync(CreateTestSong());
        await ScrobbleAsync(CreateTestSong(), DateTime.UtcNow);

        _httpMessageHandler.Requests.Should().BeEmpty();
        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.SingleAsync()).IsScrobbled.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TurnedOff_SendsNothing(bool nowPlaying, bool scrobbling)
    {
        _settingsService.Set(CoreSettings.LastFmNowPlaying, nowPlaying);
        _settingsService.Set(CoreSettings.LastFmScrobbling, scrobbling);
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK);

        if (nowPlaying) await ScrobbleAsync(CreateTestSong(), DateTime.UtcNow);
        else await _scrobblerService.UpdateNowPlayingAsync(CreateTestSong());

        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateNowPlayingAsync_WhenHttpCallThrows_DoesNotThrow()
    {
        SetupValidCredentials();
        _httpMessageHandler.SendAsyncFunc = (_, _) => throw new HttpRequestException("Network error");

        await FluentActions.Awaiting(() => _scrobblerService.UpdateNowPlayingAsync(CreateTestSong())).Should().NotThrowAsync();
    }

    [Fact]
    public async Task SubmitPendingAsync_RunsAtTheSameTime_SendsEachListenOnce()
    {
        SetupValidCredentials();
        var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _httpMessageHandler.SendAsyncFunc = async (_, _) =>
        {
            firstRequest.TrySetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(SuccessfulResponse, Encoding.UTF8, "application/xml") };
        };
        await SeedPendingListensAsync((1, DateTime.UtcNow.AddMinutes(-10)), (2, DateTime.UtcNow.AddMinutes(-5)));

        // The queue run started by a listen reaching the threshold, while the retry loop runs the queue too.
        var realTime = _scrobblerService.SubmitPendingAsync(CancellationToken.None);
        await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retry = _scrobblerService.SubmitPendingAsync(CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(realTime, retry).WaitAsync(TimeSpan.FromSeconds(5));

        _httpMessageHandler.Requests.Should().HaveCount(2);
    }
    [Fact]
    public async Task UpdateNowPlayingAsync_WithMinimalSongData_SendsCorrectParameters()
    {
        var song = new Song { Title = "Title Only" };
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK);

        await _scrobblerService.UpdateNowPlayingAsync(song);

        _httpMessageHandler.Requests.Should().HaveCount(1);
        _capturedRequestParams.Should().NotBeNull();
        _capturedRequestParams!.Should().ContainKey("artist").WhoseValue.Should().Be("Unknown Artist");
        _capturedRequestParams.Should().NotContainKey("album");
        _capturedRequestParams.Should().NotContainKey("duration");
        _capturedRequestParams.Should().NotContainKey("mbid");
    }

    [Fact]
    public async Task SubmitPendingAsync_SendsMusicBrainzIdTrackNumberAndDifferingAlbumArtist()
    {
        var song = CreateTestSong();
        song.MusicBrainzTrackId = "33333333-3333-3333-3333-333333333333";
        song.TrackNumber = 4;
        song.Album!.AlbumArtists.Clear();
        song.Album.AlbumArtists.Add(new AlbumArtist { Artist = new Artist { Name = "Various Artists" }, Order = 0 });
        song.Album.SyncDenormalizedFields();
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK);

        await ScrobbleAsync(song, DateTime.UtcNow);

        _capturedRequestParams.Should().Contain("mbid", "33333333-3333-3333-3333-333333333333")
            .And.Contain("trackNumber", "4")
            .And.Contain("albumArtist", "Various Artists");
    }

    [Fact]
    public async Task UpdateNowPlayingAsync_WithMultipleArtists_SendsPrimaryArtistName()
    {
        var artist1 = new Artist { Name = "Artist A" };
        var artist2 = new Artist { Name = "Artist B" };
        var song = new Song { Title = "Duo Track" };
        song.SongArtists.Add(new SongArtist { Artist = artist1, Order = 0 });
        song.SongArtists.Add(new SongArtist { Artist = artist2, Order = 1 });
        song.SyncDenormalizedFields();

        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK);

        await _scrobblerService.UpdateNowPlayingAsync(song);

        _capturedRequestParams.Should().NotBeNull();
        _capturedRequestParams!["artist"].Should().Be("Artist A");
    }

    [Theory]
    [InlineData(SuccessfulResponse)]
    [InlineData("<lfm status='failed'><error code='6'>Invalid parameters</error></lfm>")]
    [InlineData("<lfm status='ok'><scrobbles accepted='0' ignored='1'><scrobble><ignoredMessage code='3'>Timestamp too old</ignoredMessage></scrobble></scrobbles></lfm>")]
    public async Task SubmitPendingAsync_AcknowledgedOrPermanentlyRejectedListensDoNotBlockQueue(string firstResponse)
    {
        _settingsService.Set(CoreSettings.LastFmScrobbling, true);
        SetupValidCredentials();
        _httpMessageHandler.RespondSequence(
            (HttpStatusCode.OK, firstResponse),
            (HttpStatusCode.OK, SuccessfulResponse));

        await SeedPendingListensAsync((1, DateTime.UtcNow.AddMinutes(-10)), (2, DateTime.UtcNow.AddMinutes(-5)));

        var sut = CreateService();
        await sut.SubmitPendingAsync(CancellationToken.None);

        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(1L))!.IsScrobbled.Should().BeTrue();
        (await verify.ListenHistory.FindAsync(2L))!.IsScrobbled.Should().BeTrue();
        _httpMessageHandler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.Forbidden, "")]
    [InlineData(HttpStatusCode.OK, "<lfm status='failed'><error code='9'>Invalid session</error></lfm>")]
    [InlineData(HttpStatusCode.OK, "<lfm status='failed'><error code='11'>Offline</error></lfm>")]
    [InlineData(HttpStatusCode.OK, "<lfm status='failed'><error code='4'>Authentication failed</error></lfm>")]
    [InlineData(HttpStatusCode.OK, "<lfm status='failed'><error code='8'>Operation failed</error></lfm>")]
    [InlineData(HttpStatusCode.OK, "<lfm status='failed'><error code='10'>Invalid API key</error></lfm>")]
    [InlineData(HttpStatusCode.OK, "<lfm status='failed'><error code='13'>Invalid signature</error></lfm>")]
    [InlineData(HttpStatusCode.OK, "<lfm status='failed'><error code='26'>Suspended API key</error></lfm>")]
    [InlineData(HttpStatusCode.OK, "<lfm status='failed'><error code='29'>Rate limited</error></lfm>")]
    [InlineData(HttpStatusCode.OK, "<lfm status='ok'><scrobbles accepted='0' ignored='1'><scrobble><ignoredMessage code='4'>Timestamp too new</ignoredMessage></scrobble></scrobbles></lfm>")]
    [InlineData(HttpStatusCode.OK, "<lfm status='ok'><scrobbles accepted='0' ignored='1'><scrobble><ignoredMessage code='5'>Daily limit</ignoredMessage></scrobble></scrobbles></lfm>")]
    [InlineData(HttpStatusCode.OK, "<lfm status='ok' />")]
    [InlineData(HttpStatusCode.BadRequest, "<lfm status='failed'><error code='16'>Temporary error</error></lfm>")]
    [InlineData(HttpStatusCode.OK, "<html>Unexpected proxy response</html>")]
    [InlineData(HttpStatusCode.OK, "")]
    public async Task SubmitPendingAsync_RecoverableFailurePreservesRemainingListensForRetry(HttpStatusCode status, string body)
    {
        _settingsService.Set(CoreSettings.LastFmScrobbling, true);
        SetupValidCredentials();

        _httpMessageHandler.RespondSequence(
            (HttpStatusCode.OK, SuccessfulResponse),
            (status, body));

        await SeedPendingListensAsync((100, DateTime.UtcNow.AddMinutes(-30)), (101, DateTime.UtcNow.AddMinutes(-20)), (102, DateTime.UtcNow.AddMinutes(-10)));

        var sut = CreateService();
        await sut.SubmitPendingAsync(CancellationToken.None);

        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(100L))!.IsScrobbled.Should().BeTrue();
        (await verify.ListenHistory.FindAsync(101L))!.IsScrobbled.Should().BeFalse();
        (await verify.ListenHistory.FindAsync(102L))!.IsScrobbled.Should().BeFalse();
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            _httpMessageHandler.Requests.Should().HaveCount(2);

        SetupHttpResponse(HttpStatusCode.OK);
        await sut.SubmitPendingAsync(CancellationToken.None);
        (await verify.ListenHistory.AllAsync(listen => listen.IsScrobbled)).Should().BeTrue();
    }

    [Fact]
    public async Task SubmitPendingAsync_ListenTime_IsReadBackAsUtc()
    {
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK);

        await ScrobbleAsync(CreateTestSong(), new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Unspecified));

        _capturedRequestParams!["timestamp"].Should().Be("1672574400");
    }

    // A listen reaching the threshold is sent through the queue, as the coordinator does.
    private async Task ScrobbleAsync(Song song, DateTime startTime)
    {
        await SeedPendingListensAsync(song, (1, startTime));
        await _scrobblerService.SubmitPendingAsync(CancellationToken.None);
    }

    private Task SeedPendingListensAsync(params (long Id, DateTime Timestamp)[] listens) =>
        SeedPendingListensAsync(CreateTestSong(), listens);

    private async Task SeedPendingListensAsync(Song song, params (long Id, DateTime Timestamp)[] listens)
    {
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        song.FilePath = "C:/Music/song.mp3";
        song.Folder = new Folder { Name = "Music", Path = "C:/Music" };
        context.ListenHistory.AddRange(listens.Select(listen => new ListenHistory
        {
            Id = listen.Id,
            Song = song,
            ListenTimestampUtc = listen.Timestamp,
            IsEligibleForScrobbling = true
        }));
        await context.SaveChangesAsync();
    }

    private void SetupValidCredentials()
    {
        _apiKeyService.GetApiKeyAsync("lastfm").Returns(Task.FromResult<string?>(ApiKey));
        _apiKeyService.GetApiKeyAsync("lastfm-secret").Returns(Task.FromResult<string?>(ApiSecret));
        _credentials.RetrieveCredential("Nagi/LastFm").Returns(("user", SessionKey));
    }
    private void SetupHttpResponse(HttpStatusCode statusCode)
    {
        _httpMessageHandler.SendAsyncFunc = async (req, _) =>
        {
            if (req.Content != null) _capturedRequestParams = await GetRequestParameters(req);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(statusCode == HttpStatusCode.OK
                    ? _capturedRequestParams?["method"] == "track.updateNowPlaying" ? NowPlayingResponse : SuccessfulResponse
                    : "<lfm status='failed'><error code='6'>Invalid parameters</error></lfm>",
                    Encoding.UTF8, "application/xml")
            };
        };
    }
    private static Song CreateTestSong()
    {
        var artist = new Artist { Name = "Test Artist" };
        var album = new Album { Title = "Test Album" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = artist, Order = 0 });

        var song = new Song
        {
            Title = "Test Title",
            Album = album,
            Duration = TimeSpan.FromSeconds(180)
        };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        song.SyncDenormalizedFields();
        album.SyncDenormalizedFields();
        return song;
    }
    private static async Task<Dictionary<string, string>> GetRequestParameters(HttpRequestMessage request)
    {
        var content = await request.Content!.ReadAsStringAsync();
        var parameters = HttpUtility.ParseQueryString(content);
        return parameters.AllKeys.ToDictionary(key => key!, key => parameters[key]!);
    }

}
