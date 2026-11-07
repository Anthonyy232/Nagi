using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Models;
using Nagi.Core.Scrobbling;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class ListenBrainzScrobblerServiceTests : IDisposable
{
    private const string Token = "tok-abc";
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILogger<ListenBrainzScrobblerService> _logger;
    private readonly ICredentialLockerService _credentials;
    private readonly SettingsService _settingsService;
    private readonly IAppInfoService _appInfoService = Substitute.For<IAppInfoService>();

    private string? _capturedRequestBody;

    private readonly ProviderPipelineProvider _pipelines;

    public ListenBrainzScrobblerServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _credentials = Substitute.For<ICredentialLockerService>();
        _settingsService = TestSettings.Create(credentials: _credentials);
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<ListenBrainzScrobblerService>>();
        _dbHelper = new DbContextFactoryTestHelper();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        SetToken(Token);
        _appInfoService.GetAppName().Returns("Nagi");
        _settingsService.Set(CoreSettings.ListenBrainzNowPlaying, true);
        _settingsService.Set(CoreSettings.ListenBrainzScrobbling, true);
        _settingsService.Set(CoreSettings.ListenBrainzEnabledSinceUtc, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ListenBrainz);
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpMessageHandler.Dispose();
        _dbHelper.Dispose();
    }

    private ListenBrainzScrobblerService CreateService()
    {
        return new ListenBrainzScrobblerService(
            _httpClientFactory,
            _pipelines,
            _dbHelper.ContextFactory,
            _settingsService,
            _appInfoService,
            _logger);
    }

    [Fact]
    public async Task SubmitPendingAsync_SendsMusicBrainzIdsAndClientDetails()
    {
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        _appInfoService.GetAppVersion().Returns("2.3.0");
        var song = CreateTestSong();
        song.MusicBrainzTrackId = "33333333-3333-3333-3333-333333333333";
        song.MusicBrainzReleaseId = "44444444-4444-4444-4444-444444444444";
        song.TrackNumber = 4;

        await SubmitAsync(song, DateTime.UtcNow);

        var info = JsonSerializer.Deserialize<ListenBrainzSubmitPayload>(_capturedRequestBody!)!
            .Payload[0].TrackMetadata.AdditionalInfo;
        info.Should().BeEquivalentTo(new ListenBrainzAdditionalInfo
        {
            RecordingMbid = "33333333-3333-3333-3333-333333333333",
            ReleaseMbid = "44444444-4444-4444-4444-444444444444",
            TrackNumber = 4,
            DurationMs = 180_000,
            MediaPlayer = "Nagi",
            SubmissionClient = "Nagi",
            SubmissionClientVersion = "2.3.0"
        });
        _capturedRequestBody.Should().NotContain("release_group_mbid");
    }

    [Fact]
    public async Task SubmitPendingAsync_OneListen_PostsCorrectJsonAndAuthHeader()
    {
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        // Stored without a time zone, as SQLite keeps it.
        var started = new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Unspecified);

        await SubmitAsync(CreateTestSong(), started);

        _httpMessageHandler.Requests.Should().HaveCount(1);
        var req = _httpMessageHandler.Requests[0];
        req.Method.Should().Be(HttpMethod.Post);
        req.RequestUri!.ToString().Should().Be("https://api.listenbrainz.org/1/submit-listens");
        req.Headers.Authorization.Should().NotBeNull();
        req.Headers.Authorization!.Scheme.Should().Be("Token");
        req.Headers.Authorization.Parameter.Should().Be(Token);

        _capturedRequestBody.Should().NotBeNull();
        var body = JsonSerializer.Deserialize<ListenBrainzSubmitPayload>(_capturedRequestBody!);
        body.Should().NotBeNull();
        body!.ListenType.Should().Be("single");
        body.Payload.Should().HaveCount(1);
        body.Payload[0].ListenedAt.Should().Be(1776859200L); // unix seconds for 2026-04-22T12:00:00Z
        body.Payload[0].TrackMetadata.ArtistName.Should().Be("Test Artist");
        body.Payload[0].TrackMetadata.TrackName.Should().Be("Test Title");
        body.Payload[0].TrackMetadata.ReleaseName.Should().Be("Test Album");
    }

    [Fact]
    public async Task UpdateNowPlayingAsync_UsesPlayingNowListenTypeAndOmitsListenedAt()
    {
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");

        await CreateService().UpdateNowPlayingAsync(CreateTestSong());

        _capturedRequestBody.Should().NotBeNull();
        var body = JsonSerializer.Deserialize<ListenBrainzSubmitPayload>(_capturedRequestBody!);
        body!.ListenType.Should().Be("playing_now");
        body.Payload[0].ListenedAt.Should().BeNull();
        // listened_at must be OMITTED from the wire format for playing_now.
        _capturedRequestBody.Should().NotContain("listened_at");
    }

    [Fact]
    public async Task SubmitPendingAsync_ListenWithoutATitle_IsDroppedWithoutHttpCall()
    {
        var artist = new Artist { Name = "Some Artist" };
        var song = new Song { Title = string.Empty };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        song.SyncDenormalizedFields();

        await SubmitAsync(song, DateTime.UtcNow);

        _httpMessageHandler.Requests.Should().BeEmpty();
        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.SingleAsync()).IsSubmittedToListenBrainz.Should().BeTrue();
    }

    [Fact]
    public async Task SubmitPendingAsync_RespectsCustomServerUrl()
    {
        _settingsService.Set(CoreSettings.ListenBrainzServerUrl, "https://lb.example.com");
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");

        await SubmitAsync(CreateTestSong(), DateTime.UtcNow);

        _httpMessageHandler.Requests.Should().HaveCount(1);
        _httpMessageHandler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://lb.example.com/1/submit-listens");
    }

    [Fact]
    public async Task ValidateTokenAsync_OnValidResponse_ReturnsUsername()
    {

        SetupHttpResponse(HttpStatusCode.OK, "{\"valid\":true,\"user_name\":\"alice\"}");

        var service = CreateService();
        var result = await service.ValidateTokenAsync("tok-abc", null);

        result.IsValid.Should().BeTrue();
        result.Username.Should().Be("alice");
        result.Error.Should().BeNull();
        _httpMessageHandler.Requests.Should().HaveCount(1);
        _httpMessageHandler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Token");
        _httpMessageHandler.Requests[0].Headers.Authorization!.Parameter.Should().Be("tok-abc");
    }

    [Fact]
    public async Task ValidateTokenAsync_OnInvalidResponse_ReturnsError()
    {

        SetupHttpResponse(HttpStatusCode.OK, "{\"valid\":false,\"message\":\"Token invalid\"}");

        var service = CreateService();
        var result = await service.ValidateTokenAsync("bad", null);

        result.IsValid.Should().BeFalse();
        result.Username.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error.Should().ContainEquivalentOf("invalid");
    }


    [Theory]
    [InlineData("tok", false)]
    [InlineData(null, true)]
    public async Task SubmitPendingAsync_WithoutTheTokenOrScrobbling_SendsNothing(string? token, bool scrobbling)
    {
        SetToken(token);
        _settingsService.Set(CoreSettings.ListenBrainzScrobbling, scrobbling);
        SetupHttpResponse(HttpStatusCode.OK, "{}");

        await SubmitAsync(CreateTestSong(), DateTime.UtcNow);

        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitPendingAsync_BatchesPendingListensIntoOneRequest()
    {
        SetToken("tok");
        _settingsService.Set(CoreSettings.ListenBrainzScrobbling, true);
        _settingsService.Set(CoreSettings.ListenBrainzEnabledSinceUtc, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");

        await SeedPendingListensAsync((1, DateTime.UtcNow.AddMinutes(-10)), (2, DateTime.UtcNow.AddMinutes(-5)));

        var sut = CreateService();
        await sut.SubmitPendingAsync(CancellationToken.None);

        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(1L))!.IsSubmittedToListenBrainz.Should().BeTrue();
        (await verify.ListenHistory.FindAsync(2L))!.IsSubmittedToListenBrainz.Should().BeTrue();

        _httpMessageHandler.Requests.Should().HaveCount(1);
        _capturedRequestBody.Should().NotBeNull();
        var body = JsonSerializer.Deserialize<ListenBrainzSubmitPayload>(_capturedRequestBody!);
        body!.ListenType.Should().Be("import");
        body.Payload.Should().HaveCount(2);
    }

    [Fact]
    public async Task SubmitPendingAsync_SkipsListensBeforeEnabledSince()
    {
        SetToken("tok");
        _settingsService.Set(CoreSettings.ListenBrainzScrobbling, true);
        var cutoff = DateTime.UtcNow;
        _settingsService.Set(CoreSettings.ListenBrainzEnabledSinceUtc, cutoff);

        await SeedPendingListensAsync((10, cutoff.AddHours(-1)));

        var sut = CreateService();
        await sut.SubmitPendingAsync(CancellationToken.None);

        _httpMessageHandler.Requests.Should().BeEmpty();
        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(10L))!.IsSubmittedToListenBrainz.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task SubmitPendingAsync_RecoverableBatchFailurePreservesListensForRetry(HttpStatusCode status)
    {
        SetToken("tok");
        _settingsService.Set(CoreSettings.ListenBrainzScrobbling, true);
        _settingsService.Set(CoreSettings.ListenBrainzEnabledSinceUtc, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        SetupHttpResponse(status, "{}");

        await SeedPendingListensAsync((100, DateTime.UtcNow.AddMinutes(-30)), (101, DateTime.UtcNow.AddMinutes(-20)), (102, DateTime.UtcNow.AddMinutes(-10)));

        var sut = CreateService();
        await sut.SubmitPendingAsync(CancellationToken.None);

        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(100L))!.IsSubmittedToListenBrainz.Should().BeFalse();
        (await verify.ListenHistory.FindAsync(101L))!.IsSubmittedToListenBrainz.Should().BeFalse();
        (await verify.ListenHistory.FindAsync(102L))!.IsSubmittedToListenBrainz.Should().BeFalse();
        if (status is HttpStatusCode.Unauthorized)
            _httpMessageHandler.Requests.Should().ContainSingle();

        SetupHttpResponse(HttpStatusCode.OK, "{}");
        await sut.SubmitPendingAsync(CancellationToken.None);
        (await verify.ListenHistory.AllAsync(listen => listen.IsSubmittedToListenBrainz)).Should().BeTrue();
    }

    [Fact]
    public async Task SubmitPendingAsync_RejectedBatch_IsolatesBadListenAndKeepsUnacknowledgedListens()
    {
        _settingsService.Set(CoreSettings.ListenBrainzScrobbling, true);
        _settingsService.Set(CoreSettings.ListenBrainzEnabledSinceUtc, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _httpMessageHandler.RespondSequence(
            (HttpStatusCode.BadRequest, "{}"),
            (HttpStatusCode.BadRequest, "{}"),
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.ServiceUnavailable, "{}"));
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = CreateTestSong();
            song.FilePath = "C:/Music/queue.mp3";
            song.Folder = new Folder { Name = "Music", Path = "C:/Music" };
            context.ListenHistory.AddRange(Enumerable.Range(1, 3).Select(id => new ListenHistory
            {
                Id = id,
                Song = song,
                IsEligibleForScrobbling = true,
                ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-10 + id)
            }));
            await context.SaveChangesAsync();
        }
        var service = CreateService();

        await service.SubmitPendingAsync(CancellationToken.None);

        using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.OrderBy(listen => listen.Id).Select(listen => listen.IsSubmittedToListenBrainz).ToListAsync())
            .Should().Equal(true, true, false);
        _httpMessageHandler.Requests.Should().HaveCount(4);

        SetupHttpResponse(HttpStatusCode.OK, "{}");
        await service.SubmitPendingAsync(CancellationToken.None);

        (await verify.ListenHistory.AllAsync(listen => listen.IsSubmittedToListenBrainz)).Should().BeTrue();
        _httpMessageHandler.Requests.Should().HaveCount(5);
    }

    // A listen reaching the threshold is sent through the queue, as the coordinator does.
    private async Task SubmitAsync(Song song, DateTime startTime)
    {
        await SeedPendingListensAsync(song, (1, startTime));
        await CreateService().SubmitPendingAsync(CancellationToken.None);
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

    private void SetToken(string? token) =>
        _credentials.RetrieveCredential("Nagi/ListenBrainz").Returns(token is null ? null : ("token", token));

    private void SetupHttpResponse(HttpStatusCode statusCode, string body)
    {
        _httpMessageHandler.SendAsyncFunc = async (req, ct) =>
        {
            if (req.Content is not null)
                _capturedRequestBody = await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
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
        return song;
    }

}
