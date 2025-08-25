using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly LastFmScrobblerService _scrobblerService;
    private readonly ISettingsService _settingsService;

    private Dictionary<string, string>? _capturedRequestParams;

    public LastFmScrobblerServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _settingsService = Substitute.For<ISettingsService>();
        _httpMessageHandler = new TestHttpMessageHandler();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _scrobblerService = new LastFmScrobblerService(_httpClientFactory, _apiKeyService, _settingsService);
    }

    public void Dispose()
    {
        _httpMessageHandler.Dispose();
        GC.SuppressFinalize(this);
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
            { "artist", song.Artist!.Name },
            { "track", song.Title },
            { "album", song.Album!.Title },
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
            { "artist", song.Artist!.Name },
            { "track", song.Title },
            { "album", song.Album!.Title },
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
    ///     the Last.fm API returns a non-success status code.
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
        _httpMessageHandler.Requests.Should().HaveCount(1);
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
        var song = new Song { Title = "Title Only", Artist = null };
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
        return new Song
        {
            Title = "Test Title",
            Artist = new Artist { Name = "Test Artist" },
            Album = new Album { Title = "Test Album" },
            Duration = TimeSpan.FromSeconds(180)
        };
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