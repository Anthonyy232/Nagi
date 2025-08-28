using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Contains unit tests for the <see cref="LastFmAuthService" />.
///     These tests verify the service's ability to handle the Last.fm authentication flow,
///     including token retrieval, session creation, API signature generation, and error handling.
/// </summary>
public class LastFmAuthServiceTests : IDisposable
{
    private const string ApiKey = "test-api-key";
    private const string ApiSecret = "test-api-secret";
    private readonly IApiKeyService _apiKeyService;
    private readonly LastFmAuthService _authService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILogger<LastFmAuthService> _logger;

    public LastFmAuthServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<LastFmAuthService>>();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _authService = new LastFmAuthService(_httpClientFactory, _apiKeyService, _logger);
    }

    public void Dispose()
    {
        _httpMessageHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Verifies that <see cref="LastFmAuthService.GetAuthenticationTokenAsync" /> successfully retrieves
    ///     a token and constructs the correct authentication URL when provided with valid credentials.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> Valid API key and secret are available, and the Last.fm API returns a successful response.
    ///     <br />
    ///     <b>Expected Result:</b> The method returns the token and a correctly formatted user authentication URL.
    ///     The underlying HTTP request should be correctly formed and signed.
    /// </remarks>
    [Fact]
    public async Task GetAuthenticationTokenAsync_WithValidCredentialsAndResponse_ReturnsTokenAndUrl()
    {
        // Arrange
        const string token = "auth-token-123";
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK, new { token });

        var expectedParams = new Dictionary<string, string> { { "method", "auth.getToken" }, { "api_key", ApiKey } };
        var expectedSignature = CreateSignature(expectedParams, ApiSecret);

        // Act
        var result = await _authService.GetAuthenticationTokenAsync();

        // Assert
        result.Should().NotBeNull();
        result!.Value.Token.Should().Be(token);
        result!.Value.AuthUrl.Should().Be($"https://www.last.fm/api/auth/?api_key={ApiKey}&token={token}");

        _httpMessageHandler.Requests.Should().HaveCount(1);
        var requestUri = _httpMessageHandler.Requests[0].RequestUri!.ToString();
        requestUri.Should().Contain("method=auth.getToken");
        requestUri.Should().Contain($"api_key={ApiKey}");
        requestUri.Should().Contain($"api_sig={expectedSignature}");
    }

    /// <summary>
    ///     Verifies that <see cref="LastFmAuthService.GetSessionAsync" /> successfully retrieves a user
    ///     session when provided with a valid token and credentials.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> A valid auth token is provided, credentials are valid, and the Last.fm API
    ///     returns a successful session response.
    ///     <br />
    ///     <b>Expected Result:</b> The method returns the username and session key. The underlying HTTP
    ///     request should be correctly formed and signed.
    /// </remarks>
    [Fact]
    public async Task GetSessionAsync_WithValidTokenAndResponse_ReturnsUsernameAndSessionKey()
    {
        // Arrange
        const string token = "auth-token-123";
        const string username = "testuser";
        const string sessionKey = "session-key-456";
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK, new { session = new { name = username, key = sessionKey } });

        var expectedParams = new Dictionary<string, string>
        {
            { "method", "auth.getSession" },
            { "api_key", ApiKey },
            { "token", token }
        };
        var expectedSignature = CreateSignature(expectedParams, ApiSecret);

        // Act
        var result = await _authService.GetSessionAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Username.Should().Be(username);
        result!.Value.SessionKey.Should().Be(sessionKey);

        _httpMessageHandler.Requests.Should().HaveCount(1);
        var requestUri = _httpMessageHandler.Requests[0].RequestUri!.ToString();
        requestUri.Should().Contain("method=auth.getSession");
        requestUri.Should().Contain($"api_key={ApiKey}");
        requestUri.Should().Contain($"token={token}");
        requestUri.Should().Contain($"api_sig={expectedSignature}");
    }

    /// <summary>
    ///     Verifies that API calls return null and do not make an HTTP request when essential
    ///     credentials (API key or secret) are missing.
    /// </summary>
    [Theory]
    [InlineData(null, ApiSecret)]
    [InlineData(ApiKey, null)]
    public async Task ApiCall_WhenCredentialsAreMissing_ReturnsNullAndMakesNoHttpCall(string? apiKey, string? apiSecret)
    {
        // Arrange
        _apiKeyService.GetApiKeyAsync("lastfm").Returns(Task.FromResult(apiKey));
        _apiKeyService.GetApiKeyAsync("lastfm-secret").Returns(Task.FromResult(apiSecret));

        // Act
        var tokenResult = await _authService.GetAuthenticationTokenAsync();
        var sessionResult = await _authService.GetSessionAsync("any-token");

        // Assert
        tokenResult.Should().BeNull();
        sessionResult.Should().BeNull();
        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     Verifies that <see cref="LastFmAuthService.GetAuthenticationTokenAsync" /> returns null when
    ///     the Last.fm API returns a non-success status code.
    /// </summary>
    [Fact]
    public async Task GetAuthenticationTokenAsync_WhenApiCallFails_ReturnsNull()
    {
        // Arrange
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.InternalServerError, null);

        // Act
        var result = await _authService.GetAuthenticationTokenAsync();

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that <see cref="LastFmAuthService.GetSessionAsync" /> returns null when the API
    ///     response is malformed or missing expected fields.
    /// </summary>
    [Fact]
    public async Task GetSessionAsync_WhenResponseIsMalformed_ReturnsNull()
    {
        // Arrange
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK, new { session = new { name = "user" } }); // Missing 'key'

        // Act
        var result = await _authService.GetSessionAsync("any-token");

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that API calls return null when the underlying <see cref="HttpClient" /> call
    ///     throws an exception.
    /// </summary>
    [Fact]
    public async Task ApiCall_WhenHttpCallThrowsException_ReturnsNull()
    {
        // Arrange
        SetupValidCredentials();
        _httpMessageHandler.SendAsyncFunc = (_, _) => throw new HttpRequestException("Network error");

        // Act
        var result = await _authService.GetAuthenticationTokenAsync();

        // Assert
        result.Should().BeNull();
    }

    #region Helper Methods

    /// <summary>
    ///     Configures the mock <see cref="IApiKeyService" /> to return valid Last.fm credentials.
    /// </summary>
    private void SetupValidCredentials()
    {
        _apiKeyService.GetApiKeyAsync("lastfm").Returns(Task.FromResult<string?>(ApiKey));
        _apiKeyService.GetApiKeyAsync("lastfm-secret").Returns(Task.FromResult<string?>(ApiSecret));
    }

    /// <summary>
    ///     Configures the mock <see cref="TestHttpMessageHandler" /> to return a specific HTTP response.
    /// </summary>
    private void SetupHttpResponse(HttpStatusCode statusCode, object? content)
    {
        var response = new HttpResponseMessage(statusCode);
        if (content != null)
        {
            var jsonContent = JsonSerializer.Serialize(content);
            response.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        }

        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(response);
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

    #endregion
}