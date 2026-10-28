using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
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
    private readonly ISettingsService _settingsService;
    private readonly IAppInfoService _appInfoService = Substitute.For<IAppInfoService>();

    private string? _capturedRequestBody;

    private readonly ProviderPipelineProvider _pipelines;

    public ListenBrainzScrobblerServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _settingsService = Substitute.For<ISettingsService>();
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<ListenBrainzScrobblerService>>();
        _dbHelper = new DbContextFactoryTestHelper();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _settingsService.GetListenBrainzUserTokenAsync().Returns(Task.FromResult<string?>(Token));
        _settingsService.GetListenBrainzServerUrlAsync().Returns(Task.FromResult<string?>(null));

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
    public async Task SubmitListenAsync_SendsMusicBrainzIdsAndClientDetails()
    {
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        _appInfoService.GetAppVersion().Returns("2.3.0");
        var song = CreateTestSong();
        song.MusicBrainzTrackId = "33333333-3333-3333-3333-333333333333";
        song.MusicBrainzReleaseId = "44444444-4444-4444-4444-444444444444";
        song.TrackNumber = 4;

        await CreateService().SubmitListenAsync(song, DateTime.UtcNow);

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
    public async Task SubmitListenAsync_PostsCorrectJsonAndAuthHeader()
    {

        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        var started = new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Utc);

        var service = CreateService();
        var ok = await service.SubmitListenAsync(CreateTestSong(), started);

        ok.Should().BeTrue();
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

        var service = CreateService();
        var ok = await service.UpdateNowPlayingAsync(CreateTestSong());

        ok.Should().BeTrue();
        _capturedRequestBody.Should().NotBeNull();
        var body = JsonSerializer.Deserialize<ListenBrainzSubmitPayload>(_capturedRequestBody!);
        body!.ListenType.Should().Be("playing_now");
        body.Payload[0].ListenedAt.Should().BeNull();
        // listened_at must be OMITTED from the wire format for playing_now.
        _capturedRequestBody.Should().NotContain("listened_at");
    }

    [Fact]
    public async Task SubmitListenAsync_On401_ReturnsFalse()
    {

        SetupHttpResponse(HttpStatusCode.Unauthorized, "{\"error\":\"invalid token\"}");

        var service = CreateService();
        var ok = await service.SubmitListenAsync(CreateTestSong(), DateTime.UtcNow);

        ok.Should().BeFalse();
        _httpMessageHandler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task SubmitListenAsync_WithMissingRequiredMetadata_ReturnsFalseWithoutHttpCall()
    {

        var artist = new Artist { Name = "Some Artist" };
        var song = new Song { Title = string.Empty };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        song.SyncDenormalizedFields();

        var service = CreateService();
        var ok = await service.SubmitListenAsync(song, DateTime.UtcNow);

        ok.Should().BeFalse();
        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitListenAsync_RespectsCustomServerUrl()
    {

        _settingsService.GetListenBrainzServerUrlAsync().Returns(Task.FromResult<string?>("https://lb.example.com"));
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");

        var service = CreateService();
        await service.SubmitListenAsync(CreateTestSong(), DateTime.UtcNow);

        _httpMessageHandler.Requests.Should().HaveCount(1);
        _httpMessageHandler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://lb.example.com/1/submit-listens");
    }

    [Fact]
    public async Task ValidateTokenAsync_OnValidResponse_ReturnsUsername()
    {

        SetupHttpResponse(HttpStatusCode.OK, "{\"valid\":true,\"user_name\":\"alice\"}");

        var service = CreateService();
        var result = await service.ValidateTokenAsync("tok-abc");

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
        var result = await service.ValidateTokenAsync("bad");

        result.IsValid.Should().BeFalse();
        result.Username.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error.Should().ContainEquivalentOf("invalid");
    }

    [Fact]
    public async Task IListenSubmitter_IsEnabledAsync_RequiresTokenAndScrobblingToggle()
    {
        _settingsService.GetListenBrainzUserTokenAsync().Returns(Task.FromResult<string?>("tok"));
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(true);
        IListenSubmitter sut = CreateService();
        (await sut.IsEnabledAsync()).Should().BeTrue();

        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(false);
        (await sut.IsEnabledAsync()).Should().BeFalse();

        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(true);
        _settingsService.GetListenBrainzUserTokenAsync().Returns(Task.FromResult<string?>(null));
        (await sut.IsEnabledAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessPendingListensAsync_BatchesPendingListensIntoOneRequest()
    {
        _settingsService.GetListenBrainzUserTokenAsync().Returns(Task.FromResult<string?>("tok"));
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(true);
        _settingsService.GetListenBrainzEnabledSinceUtcAsync()
            .Returns(Task.FromResult<DateTime?>(DateTime.UtcNow.AddDays(-1)));
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");

        await SeedPendingListensAsync((1, DateTime.UtcNow.AddMinutes(-10)), (2, DateTime.UtcNow.AddMinutes(-5)));

        IListenSubmitter sut = CreateService();
        await sut.ProcessPendingListensAsync(CancellationToken.None);

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
    public async Task ProcessPendingListensAsync_SkipsListensBeforeEnabledSince()
    {
        _settingsService.GetListenBrainzUserTokenAsync().Returns(Task.FromResult<string?>("tok"));
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(true);
        var cutoff = DateTime.UtcNow;
        _settingsService.GetListenBrainzEnabledSinceUtcAsync().Returns(Task.FromResult<DateTime?>(cutoff));

        await SeedPendingListensAsync((10, cutoff.AddHours(-1)));

        IListenSubmitter sut = CreateService();
        await sut.ProcessPendingListensAsync(CancellationToken.None);

        _httpMessageHandler.Requests.Should().BeEmpty();
        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(10L))!.IsSubmittedToListenBrainz.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ProcessPendingListensAsync_RecoverableBatchFailurePreservesListensForRetry(HttpStatusCode status)
    {
        _settingsService.GetListenBrainzUserTokenAsync().Returns(Task.FromResult<string?>("tok"));
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(true);
        _settingsService.GetListenBrainzEnabledSinceUtcAsync()
            .Returns(Task.FromResult<DateTime?>(DateTime.UtcNow.AddDays(-1)));

        SetupHttpResponse(status, "{}");

        await SeedPendingListensAsync((100, DateTime.UtcNow.AddMinutes(-30)), (101, DateTime.UtcNow.AddMinutes(-20)), (102, DateTime.UtcNow.AddMinutes(-10)));

        IListenSubmitter sut = CreateService();
        await sut.ProcessPendingListensAsync(CancellationToken.None);

        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(100L))!.IsSubmittedToListenBrainz.Should().BeFalse();
        (await verify.ListenHistory.FindAsync(101L))!.IsSubmittedToListenBrainz.Should().BeFalse();
        (await verify.ListenHistory.FindAsync(102L))!.IsSubmittedToListenBrainz.Should().BeFalse();
        if (status is HttpStatusCode.Unauthorized)
            _httpMessageHandler.Requests.Should().ContainSingle();

        SetupHttpResponse(HttpStatusCode.OK, "{}");
        await sut.ProcessPendingListensAsync(CancellationToken.None);
        (await verify.ListenHistory.AllAsync(listen => listen.IsSubmittedToListenBrainz)).Should().BeTrue();
    }

    [Fact]
    public async Task ProcessPendingListensAsync_RejectedBatch_IsolatesBadListenAndKeepsUnacknowledgedListens()
    {
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(true);
        _settingsService.GetListenBrainzEnabledSinceUtcAsync().Returns(DateTime.UtcNow.AddDays(-1));
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
        IListenSubmitter service = CreateService();

        await service.ProcessPendingListensAsync(CancellationToken.None);

        using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.OrderBy(listen => listen.Id).Select(listen => listen.IsSubmittedToListenBrainz).ToListAsync())
            .Should().Equal(true, true, false);
        _httpMessageHandler.Requests.Should().HaveCount(4);

        SetupHttpResponse(HttpStatusCode.OK, "{}");
        await service.ProcessPendingListensAsync(CancellationToken.None);

        (await verify.ListenHistory.AllAsync(listen => listen.IsSubmittedToListenBrainz)).Should().BeTrue();
        _httpMessageHandler.Requests.Should().HaveCount(5);
    }

    private async Task SeedPendingListensAsync(params (long Id, DateTime Timestamp)[] listens)
    {
        using var context = _dbHelper.ContextFactory.CreateDbContext();
        var song = CreateTestSong();
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
