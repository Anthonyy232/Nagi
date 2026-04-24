using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Contains unit tests for the <see cref="ListenBrainzScrobblerService" />.
///     These tests verify submit-listens request shape (URL, auth header, JSON body),
///     "playing_now" versus "single" behavior, validation responses, short-circuit paths,
///     and the <see cref="IListenSubmitter" /> queue-processing surface.
/// </summary>
public class ListenBrainzScrobblerServiceTests : IDisposable
{
    private const string Token = "tok-abc";
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILogger<ListenBrainzScrobblerService> _logger;
    private readonly ISettingsService _settingsService;

    private string? _capturedRequestBody;

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
    }

    public void Dispose()
    {
        _httpMessageHandler.Dispose();
        _dbHelper.Dispose();
        GC.SuppressFinalize(this);
    }

    private ListenBrainzScrobblerService CreateService(IDbContextFactory<MusicDbContext>? contextFactory = null)
    {
        return new ListenBrainzScrobblerService(
            _httpClientFactory,
            contextFactory ?? Substitute.For<IDbContextFactory<MusicDbContext>>(),
            _settingsService,
            _logger);
    }

    [Fact]
    public async Task SubmitListenAsync_PostsCorrectJsonAndAuthHeader()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        var started = new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var service = CreateService();
        var ok = await service.SubmitListenAsync(CreateTestSong(), started);

        // Assert
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
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");

        // Act
        var service = CreateService();
        var ok = await service.UpdateNowPlayingAsync(CreateTestSong());

        // Assert
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
        // Arrange
        SetupHttpResponse(HttpStatusCode.Unauthorized, "{\"error\":\"invalid token\"}");

        // Act
        var service = CreateService();
        var ok = await service.SubmitListenAsync(CreateTestSong(), DateTime.UtcNow);

        // Assert
        ok.Should().BeFalse();
        _httpMessageHandler.Requests.Should().HaveCount(1); // 401 is not retryable
    }

    [Fact]
    public async Task SubmitListenAsync_WithMissingRequiredMetadata_ReturnsFalseWithoutHttpCall()
    {
        // Arrange — a song with a blank title must not be scrobbled.
        var artist = new Artist { Name = "Some Artist" };
        var song = new Song { Title = string.Empty };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        song.SyncDenormalizedFields();

        // Act
        var service = CreateService();
        var ok = await service.SubmitListenAsync(song, DateTime.UtcNow);

        // Assert
        ok.Should().BeFalse();
        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitListenAsync_RespectsCustomServerUrl()
    {
        // Arrange
        _settingsService.GetListenBrainzServerUrlAsync().Returns(Task.FromResult<string?>("https://lb.example.com"));
        SetupHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");

        // Act
        var service = CreateService();
        await service.SubmitListenAsync(CreateTestSong(), DateTime.UtcNow);

        // Assert
        _httpMessageHandler.Requests.Should().HaveCount(1);
        _httpMessageHandler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://lb.example.com/1/submit-listens");
    }

    [Fact]
    public async Task ValidateTokenAsync_OnValidResponse_ReturnsUsername()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, "{\"valid\":true,\"user_name\":\"alice\"}");

        // Act
        var service = CreateService();
        var result = await service.ValidateTokenAsync("tok-abc");

        // Assert
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
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, "{\"valid\":false,\"message\":\"Token invalid\"}");

        // Act
        var service = CreateService();
        var result = await service.ValidateTokenAsync("bad");

        // Assert
        result.IsValid.Should().BeFalse();
        result.Username.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error!.ToLowerInvariant().Should().Contain("invalid");
    }

    #region IListenSubmitter members

    [Fact]
    public void IListenSubmitter_Id_IsListenBrainz()
    {
        IListenSubmitter sut = CreateService();
        sut.Id.Should().Be("listenbrainz");
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
    public async Task ProcessPendingListensAsync_SubmitsPendingListensAndMarksSubmitted()
    {
        _settingsService.GetListenBrainzUserTokenAsync().Returns(Task.FromResult<string?>("tok"));
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(true);
        _settingsService.GetListenBrainzEnabledSinceUtcAsync()
            .Returns(Task.FromResult<DateTime?>(DateTime.UtcNow.AddDays(-1)));
        _httpMessageHandler.RespondSequence(
            (HttpStatusCode.OK, "{\"status\":\"ok\"}"),
            (HttpStatusCode.OK, "{\"status\":\"ok\"}"));

        var songId = Guid.NewGuid();
        var folder = new Folder { Name = "F", Path = "C:/Music" };
        var artist = new Artist { Name = "Artist" };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Artists.Add(artist);
            var song = new Song
            {
                Id = songId,
                Title = "Song",
                Folder = folder,
                FilePath = "C:/Music/s.mp3"
            };
            song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
            song.SyncDenormalizedFields();
            ctx.Songs.Add(song);
            ctx.ListenHistory.Add(new ListenHistory
            {
                Id = 1,
                SongId = songId,
                ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-10),
                IsEligibleForScrobbling = true,
                IsSubmittedToListenBrainz = false
            });
            ctx.ListenHistory.Add(new ListenHistory
            {
                Id = 2,
                SongId = songId,
                ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-5),
                IsEligibleForScrobbling = true,
                IsSubmittedToListenBrainz = false
            });
            await ctx.SaveChangesAsync();
        }

        IListenSubmitter sut = CreateService(_dbHelper.ContextFactory);
        await sut.ProcessPendingListensAsync(CancellationToken.None);

        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(1L))!.IsSubmittedToListenBrainz.Should().BeTrue();
        (await verify.ListenHistory.FindAsync(2L))!.IsSubmittedToListenBrainz.Should().BeTrue();
        _httpMessageHandler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProcessPendingListensAsync_SkipsListensBeforeEnabledSince()
    {
        _settingsService.GetListenBrainzUserTokenAsync().Returns(Task.FromResult<string?>("tok"));
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(true);
        var cutoff = DateTime.UtcNow;
        _settingsService.GetListenBrainzEnabledSinceUtcAsync().Returns(Task.FromResult<DateTime?>(cutoff));

        var songId = Guid.NewGuid();
        var folder = new Folder { Name = "F", Path = "C:/Music" };
        var artist = new Artist { Name = "Artist" };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Artists.Add(artist);
            var song = new Song
            {
                Id = songId,
                Title = "Song",
                Folder = folder,
                FilePath = "C:/Music/s.mp3"
            };
            song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
            song.SyncDenormalizedFields();
            ctx.Songs.Add(song);
            ctx.ListenHistory.Add(new ListenHistory
            {
                Id = 10,
                SongId = songId,
                ListenTimestampUtc = cutoff.AddHours(-1),
                IsEligibleForScrobbling = true,
                IsSubmittedToListenBrainz = false
            });
            await ctx.SaveChangesAsync();
        }

        IListenSubmitter sut = CreateService(_dbHelper.ContextFactory);
        await sut.ProcessPendingListensAsync(CancellationToken.None);

        _httpMessageHandler.Requests.Should().BeEmpty();
        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(10L))!.IsSubmittedToListenBrainz.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessPendingListensAsync_StopsOnFirstFailureToPreserveOrder()
    {
        _settingsService.GetListenBrainzUserTokenAsync().Returns(Task.FromResult<string?>("tok"));
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(true);
        _settingsService.GetListenBrainzEnabledSinceUtcAsync()
            .Returns(Task.FromResult<DateTime?>(DateTime.UtcNow.AddDays(-1)));

        // Submission for listen 100 succeeds (1 OK). Submission for listen 101 hits the retry helper:
        // it retries transient 500s up to MaxRetries=3, so we need three 500 responses to exhaust
        // the retries and have SubmitListenAsync return false. After that, the loop should break
        // and listen 102 must NOT be submitted.
        _httpMessageHandler.RespondSequence(
            (HttpStatusCode.OK, "{\"status\":\"ok\"}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.InternalServerError, "{}"));

        var songId = Guid.NewGuid();
        var folder = new Folder { Name = "F", Path = "C:/Music" };
        var artist = new Artist { Name = "Artist" };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Artists.Add(artist);
            var song = new Song
            {
                Id = songId,
                Title = "Song",
                Folder = folder,
                FilePath = "C:/Music/s.mp3"
            };
            song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
            song.SyncDenormalizedFields();
            ctx.Songs.Add(song);
            ctx.ListenHistory.AddRange(
                new ListenHistory
                {
                    Id = 100,
                    SongId = songId,
                    ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-30),
                    IsEligibleForScrobbling = true,
                    IsSubmittedToListenBrainz = false
                },
                new ListenHistory
                {
                    Id = 101,
                    SongId = songId,
                    ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-20),
                    IsEligibleForScrobbling = true,
                    IsSubmittedToListenBrainz = false
                },
                new ListenHistory
                {
                    Id = 102,
                    SongId = songId,
                    ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-10),
                    IsEligibleForScrobbling = true,
                    IsSubmittedToListenBrainz = false
                });
            await ctx.SaveChangesAsync();
        }

        IListenSubmitter sut = CreateService(_dbHelper.ContextFactory);
        await sut.ProcessPendingListensAsync(CancellationToken.None);

        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(100L))!.IsSubmittedToListenBrainz.Should().BeTrue();
        (await verify.ListenHistory.FindAsync(101L))!.IsSubmittedToListenBrainz.Should().BeFalse();
        (await verify.ListenHistory.FindAsync(102L))!.IsSubmittedToListenBrainz.Should().BeFalse();
    }

    #endregion

    #region Helpers

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

    #endregion
}
