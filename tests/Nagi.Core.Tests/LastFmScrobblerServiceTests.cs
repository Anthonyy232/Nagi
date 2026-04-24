using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Contains unit tests for the <see cref="LastFmScrobblerService" />.
///     These tests verify the service's ability to send "now playing" updates and scrobbles
///     to the Last.fm API, ensuring correct request formatting, signature generation, and error handling.
/// </summary>
public class LastFmScrobblerServiceTests : IDisposable
{
    private const string ApiKey = "test-api-key";
    private const string ApiSecret = "test-api-secret";
    private const string SessionKey = "test-session-key";
    private readonly IApiKeyService _apiKeyService;
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILogger<LastFmScrobblerService> _logger;
    private readonly LastFmScrobblerService _scrobblerService;
    private readonly ISettingsService _settingsService;

    private Dictionary<string, string>? _capturedRequestParams;

    public LastFmScrobblerServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _settingsService = Substitute.For<ISettingsService>();
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<LastFmScrobblerService>>();
        _dbHelper = new DbContextFactoryTestHelper();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _scrobblerService = CreateService();
    }

    public void Dispose()
    {
        _httpMessageHandler.Dispose();
        _dbHelper.Dispose();
        GC.SuppressFinalize(this);
    }

    private LastFmScrobblerService CreateService(IDbContextFactory<MusicDbContext>? contextFactory = null)
    {
        return new LastFmScrobblerService(
            _httpClientFactory,
            _apiKeyService,
            _settingsService,
            contextFactory ?? Substitute.For<IDbContextFactory<MusicDbContext>>(),
            _logger);
    }

    /// <summary>
    ///     Verifies that <see cref="LastFmScrobblerService.UpdateNowPlayingAsync" /> returns true and sends
    ///     a correctly formatted request when provided with a valid song and credentials.
    /// </summary>
    [Fact]
    public async Task UpdateNowPlayingAsync_WithValidSongAndCredentials_ReturnsTrueAndSendsCorrectRequest()
    {
        // Arrange
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
        var expectedSignature = CreateSignature(expectedParams, ApiSecret);
        expectedParams.Add("api_sig", expectedSignature);

        // Act
        var result = await _scrobblerService.UpdateNowPlayingAsync(song);

        // Assert
        result.Should().BeTrue();
        _httpMessageHandler.Requests.Should().HaveCount(1);
        _httpMessageHandler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _capturedRequestParams.Should().NotBeNull();
        _capturedRequestParams.Should().BeEquivalentTo(expectedParams);
    }

    /// <summary>
    ///     Verifies that <see cref="LastFmScrobblerService.ScrobbleAsync" /> returns true and sends a
    ///     correctly formatted request when provided with a valid song, timestamp, and credentials.
    /// </summary>
    [Fact]
    public async Task ScrobbleAsync_WithValidSongAndCredentials_ReturnsTrueAndSendsCorrectRequest()
    {
        // Arrange
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
            { "timestamp", expectedTimestamp },
            { "api_key", ApiKey },
            { "sk", SessionKey }
        };
        var expectedSignature = CreateSignature(expectedParams, ApiSecret);
        expectedParams.Add("api_sig", expectedSignature);

        // Act
        var result = await _scrobblerService.ScrobbleAsync(song, startTime);

        // Assert
        result.Should().BeTrue();
        _httpMessageHandler.Requests.Should().HaveCount(1);
        _httpMessageHandler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _capturedRequestParams.Should().NotBeNull();
        _capturedRequestParams.Should().BeEquivalentTo(expectedParams);
    }

    /// <summary>
    ///     Verifies that API calls fail gracefully and do not make an HTTP request when essential
    ///     credentials (API key, secret, or session key) are missing.
    /// </summary>
    [Theory]
    [InlineData(null, ApiSecret, SessionKey)]
    [InlineData(ApiKey, null, SessionKey)]
    [InlineData(ApiKey, ApiSecret, null)]
    public async Task ApiCall_WhenCredentialsAreMissing_ReturnsFalseAndMakesNoHttpCall(string? apiKey,
        string? apiSecret, string? sessionKey)
    {
        // Arrange
        _apiKeyService.GetApiKeyAsync("lastfm").Returns(Task.FromResult(apiKey));
        _apiKeyService.GetApiKeyAsync("lastfm-secret").Returns(Task.FromResult(apiSecret));
        (string?, string?)? credentials = sessionKey != null ? ("user", sessionKey) : null;
        _settingsService.GetLastFmCredentialsAsync().Returns(Task.FromResult(credentials));

        // Act
        var nowPlayingResult = await _scrobblerService.UpdateNowPlayingAsync(CreateTestSong());
        var scrobbleResult = await _scrobblerService.ScrobbleAsync(CreateTestSong(), DateTime.UtcNow);

        // Assert
        nowPlayingResult.Should().BeFalse();
        scrobbleResult.Should().BeFalse();
        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     Verifies that <see cref="LastFmScrobblerService.UpdateNowPlayingAsync" /> returns false when
    ///     the Last.fm API returns a non-success status code (after retry attempts).
    /// </summary>
    [Fact]
    public async Task UpdateNowPlayingAsync_WhenApiCallFails_ReturnsFalse()
    {
        // Arrange
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.InternalServerError);

        // Act
        var result = await _scrobblerService.UpdateNowPlayingAsync(CreateTestSong());

        // Assert
        result.Should().BeFalse();
        _httpMessageHandler.Requests.Should().HaveCount(3); // 3 attempts due to retry on 500 error
    }

    /// <summary>
    ///     Verifies that <see cref="LastFmScrobblerService.ScrobbleAsync" /> returns false when the
    ///     Last.fm API returns a non-success status code.
    /// </summary>
    [Fact]
    public async Task ScrobbleAsync_WhenApiCallFails_ReturnsFalse()
    {
        // Arrange
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.BadRequest);

        // Act
        var result = await _scrobblerService.ScrobbleAsync(CreateTestSong(), DateTime.UtcNow);

        // Assert
        result.Should().BeFalse();
        _httpMessageHandler.Requests.Should().HaveCount(1);
    }

    /// <summary>
    ///     Verifies that API calls return false when the underlying <see cref="HttpClient" /> call
    ///     throws an exception.
    /// </summary>
    [Fact]
    public async Task ApiCall_WhenHttpCallThrowsException_ReturnsFalse()
    {
        // Arrange
        SetupValidCredentials();
        _httpMessageHandler.SendAsyncFunc = (_, _) => throw new HttpRequestException("Network error");

        // Act
        var result = await _scrobblerService.UpdateNowPlayingAsync(CreateTestSong());

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    ///     Verifies that <see cref="LastFmScrobblerService.UpdateNowPlayingAsync" /> correctly handles
    ///     <see cref="Song" /> objects with missing optional data by omitting them or using fallbacks.
    /// </summary>
    [Fact]
    public async Task UpdateNowPlayingAsync_WithMinimalSongData_SendsCorrectParameters()
    {
        // Arrange
        var song = new Song { Title = "Title Only" };
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK);

        // Act
        await _scrobblerService.UpdateNowPlayingAsync(song);

        // Assert
        _httpMessageHandler.Requests.Should().HaveCount(1);
        _capturedRequestParams.Should().NotBeNull();
        _capturedRequestParams!.Should().ContainKey("artist").WhoseValue.Should().Be("Unknown Artist");
        _capturedRequestParams.Should().NotContainKey("album");
        _capturedRequestParams.Should().NotContainKey("duration");
    }

    [Fact]
    public async Task UpdateNowPlayingAsync_WithMultipleArtists_SendsJoinedArtistName()
    {
        // Arrange
        var artist1 = new Artist { Name = "Artist A" };
        var artist2 = new Artist { Name = "Artist B" };
        var song = new Song { Title = "Duo Track" };
        song.SongArtists.Add(new SongArtist { Artist = artist1, Order = 0 });
        song.SongArtists.Add(new SongArtist { Artist = artist2, Order = 1 });
        song.SyncDenormalizedFields();

        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK);

        // Act
        await _scrobblerService.UpdateNowPlayingAsync(song);

        // Assert
        _capturedRequestParams.Should().NotBeNull();
        _capturedRequestParams!["artist"].Should().Be("Artist A");
    }

    #region IListenSubmitter members

    [Fact]
    public void IListenSubmitter_Id_IsLastFm()
    {
        IListenSubmitter sut = _scrobblerService;
        sut.Id.Should().Be("lastfm");
    }

    [Fact]
    public async Task IListenSubmitter_IsEnabledAsync_GatesOnLastFmScrobblingSetting()
    {
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        IListenSubmitter sut = _scrobblerService;
        (await sut.IsEnabledAsync()).Should().BeTrue();

        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(false);
        (await sut.IsEnabledAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessPendingListensAsync_SubmitsUnscrobbledAndMarksScrobbled()
    {
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        SetupValidCredentials();
        _httpMessageHandler.RespondSequence(
            (HttpStatusCode.OK, string.Empty),
            (HttpStatusCode.OK, string.Empty));

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
                IsScrobbled = false
            });
            ctx.ListenHistory.Add(new ListenHistory
            {
                Id = 2,
                SongId = songId,
                ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-5),
                IsEligibleForScrobbling = true,
                IsScrobbled = false
            });
            await ctx.SaveChangesAsync();
        }

        IListenSubmitter sut = CreateService(_dbHelper.ContextFactory);
        await sut.ProcessPendingListensAsync(CancellationToken.None);

        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(1L))!.IsScrobbled.Should().BeTrue();
        (await verify.ListenHistory.FindAsync(2L))!.IsScrobbled.Should().BeTrue();
        _httpMessageHandler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProcessPendingListensAsync_StopsOnFirstFailureToPreserveOrder()
    {
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        SetupValidCredentials();

        // Submission for listen 100 succeeds (1 OK). Submission for listen 101 hits the retry helper:
        // it retries transient 500s up to MaxRetries=3, so we need three 500 responses to exhaust
        // the retries and have ScrobbleAsync return false. After that, the loop should break
        // and listen 102 must NOT be submitted.
        _httpMessageHandler.RespondSequence(
            (HttpStatusCode.OK, string.Empty),
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
                    IsScrobbled = false
                },
                new ListenHistory
                {
                    Id = 101,
                    SongId = songId,
                    ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-20),
                    IsEligibleForScrobbling = true,
                    IsScrobbled = false
                },
                new ListenHistory
                {
                    Id = 102,
                    SongId = songId,
                    ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-10),
                    IsEligibleForScrobbling = true,
                    IsScrobbled = false
                });
            await ctx.SaveChangesAsync();
        }

        IListenSubmitter sut = CreateService(_dbHelper.ContextFactory);
        await sut.ProcessPendingListensAsync(CancellationToken.None);

        await using var verify = _dbHelper.ContextFactory.CreateDbContext();
        (await verify.ListenHistory.FindAsync(100L))!.IsScrobbled.Should().BeTrue();
        (await verify.ListenHistory.FindAsync(101L))!.IsScrobbled.Should().BeFalse();
        (await verify.ListenHistory.FindAsync(102L))!.IsScrobbled.Should().BeFalse();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    ///     Configures all mock services to return valid Last.fm credentials (API key, secret, and session key).
    /// </summary>
    private void SetupValidCredentials()
    {
        _apiKeyService.GetApiKeyAsync("lastfm").Returns(Task.FromResult<string?>(ApiKey));
        _apiKeyService.GetApiKeyAsync("lastfm-secret").Returns(Task.FromResult<string?>(ApiSecret));
        _settingsService.GetLastFmCredentialsAsync()
            .Returns(Task.FromResult<(string?, string?)?>(("user", SessionKey)));
    }

    /// <summary>
    ///     Configures the mock <see cref="TestHttpMessageHandler" /> to return a specific HTTP status code
    ///     and captures the outgoing request body for inspection.
    /// </summary>
    private void SetupHttpResponse(HttpStatusCode statusCode)
    {
        _httpMessageHandler.SendAsyncFunc = async (req, _) =>
        {
            if (req.Content != null) _capturedRequestParams = await GetRequestParameters(req);
            return new HttpResponseMessage(statusCode);
        };
    }

    /// <summary>
    ///     Creates a sample <see cref="Song" /> object for use in tests.
    /// </summary>
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

    /// <summary>
    ///     Creates an MD5 signature for a set of Last.fm API parameters, matching the official specification.
    /// </summary>
    private static string CreateSignature(IDictionary<string, string> parameters, string secret)
    {
        var sb = new StringBuilder();
        foreach (var kvp in parameters.OrderBy(p => p.Key))
        {
            sb.Append(kvp.Key);
            sb.Append(kvp.Value);
        }

        sb.Append(secret);
        using var md5 = MD5.Create();
        var inputBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hashBytes = md5.ComputeHash(inputBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    ///     Reads and parses the form URL-encoded content from an <see cref="HttpRequestMessage" />.
    /// </summary>
    private static async Task<Dictionary<string, string>> GetRequestParameters(HttpRequestMessage request)
    {
        var content = await request.Content!.ReadAsStringAsync();
        return content.Split('&')
            .Select(part => part.Split('='))
            .ToDictionary(
                split => WebUtility.UrlDecode(split[0]),
                split => WebUtility.UrlDecode(split[1]));
    }

    #endregion
}
