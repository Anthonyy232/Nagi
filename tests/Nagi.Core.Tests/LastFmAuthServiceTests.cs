using System.Net;
using System.Web;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class LastFmAuthServiceTests : IDisposable
{
    private const string ApiKey = "test-api-key";
    private const string ApiSecret = "test-api-secret";
    private readonly IApiKeyService _apiKeyService;
    private readonly LastFmAuthService _authService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILogger<LastFmAuthService> _logger;

    private readonly ProviderPipelineProvider _pipelines;

    public LastFmAuthServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<LastFmAuthService>>();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.LastFm);
        _authService = new LastFmAuthService(_httpClientFactory, _pipelines, _apiKeyService, _logger);
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpMessageHandler.Dispose();
    }
    [Fact]
    public async Task GetAuthenticationTokenAsync_WithValidCredentialsAndResponse_ReturnsTokenAndUrl()
    {
        const string token = "auth-token-123";
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK, new { token });

        var result = await _authService.GetAuthenticationTokenAsync();

        result.Should().NotBeNull();
        result!.Value.Token.Should().Be(token);
        result!.Value.AuthUrl.Should().Be($"https://www.last.fm/api/auth/?api_key={ApiKey}&token={token}");

        _httpMessageHandler.Requests.Should().HaveCount(1);
        var requestUri = _httpMessageHandler.Requests[0].RequestUri!.ToString();
        requestUri.Should().Contain("method=auth.getToken");
        requestUri.Should().Contain($"api_key={ApiKey}");
        requestUri.Should().Contain("api_sig=050abf1f3226ab7ef429858d6a602d70");
    }
    [Fact]
    public async Task GetSessionAsync_WithValidTokenAndResponse_ReturnsUsernameAndSessionKey()
    {
        const string token = "auth-token-123";
        const string username = "testuser";
        const string sessionKey = "session-key-456";
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK, new { session = new { name = username, key = sessionKey } });

        var result = await _authService.GetSessionAsync(token);

        result.Should().NotBeNull();
        result!.Value.Username.Should().Be(username);
        result!.Value.SessionKey.Should().Be(sessionKey);

        _httpMessageHandler.Requests.Should().HaveCount(1);
        var requestUri = _httpMessageHandler.Requests[0].RequestUri!.ToString();
        requestUri.Should().Contain("method=auth.getSession");
        requestUri.Should().Contain($"api_key={ApiKey}");
        requestUri.Should().Contain($"token={token}");
        requestUri.Should().Contain("api_sig=245ea1b9f387cc3b8018285f5271b065");
    }
    [Theory]
    [InlineData(null, ApiSecret)]
    [InlineData(ApiKey, null)]
    public async Task ApiCall_WhenCredentialsAreMissing_ReturnsNullAndMakesNoHttpCall(string? apiKey, string? apiSecret)
    {
        _apiKeyService.GetApiKeyAsync("lastfm").Returns(Task.FromResult(apiKey));
        _apiKeyService.GetApiKeyAsync("lastfm-secret").Returns(Task.FromResult(apiSecret));

        var tokenResult = await _authService.GetAuthenticationTokenAsync();
        var sessionResult = await _authService.GetSessionAsync("any-token");

        tokenResult.Should().BeNull();
        sessionResult.Should().BeNull();
        _httpMessageHandler.Requests.Should().BeEmpty();
    }
    [Fact]
    public async Task GetAuthenticationTokenAsync_WhenApiCallFails_ReturnsNull()
    {
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.InternalServerError, null);

        var result = await _authService.GetAuthenticationTokenAsync();

        result.Should().BeNull();
    }
    [Fact]
    public async Task GetSessionAsync_WhenResponseIsMalformed_ReturnsNull()
    {
        SetupValidCredentials();
        SetupHttpResponse(HttpStatusCode.OK, new { session = new { name = "user" } });

        var result = await _authService.GetSessionAsync("any-token");

        result.Should().BeNull();
    }
    [Fact]
    public async Task ApiCall_WhenHttpCallThrowsException_ReturnsNull()
    {
        SetupValidCredentials();
        _httpMessageHandler.SendAsyncFunc = (_, _) => throw new HttpRequestException("Network error");

        var result = await _authService.GetAuthenticationTokenAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task AuthenticationUrls_EncodeParameterValuesWithoutChangingTokens()
    {
        const string key = "key+with&reserved=characters";
        const string token = "token/?with&reserved=characters+#";
        _apiKeyService.GetApiKeyAsync(ServiceProviderIds.LastFm).Returns(key);
        _apiKeyService.GetApiKeyAsync(ServiceProviderIds.LastFmSecret).Returns(ApiSecret);
        SetupHttpResponse(HttpStatusCode.OK, new { token });

        var authentication = await _authService.GetAuthenticationTokenAsync();

        authentication!.Value.Token.Should().Be(token);
        var browserQuery = HttpUtility.ParseQueryString(new Uri(authentication.Value.AuthUrl).Query);
        browserQuery.AllKeys.Should().BeEquivalentTo("api_key", "token");
        browserQuery["api_key"].Should().Be(key);
        browserQuery["token"].Should().Be(token);
        HttpUtility.ParseQueryString(_httpMessageHandler.Requests[0].RequestUri!.Query)["api_key"].Should().Be(key);

        SetupHttpResponse(HttpStatusCode.OK, new { session = new { name = "user", key = "session" } });
        await _authService.GetSessionAsync(token);

        var sessionQuery = HttpUtility.ParseQueryString(_httpMessageHandler.Requests[1].RequestUri!.Query);
        sessionQuery.AllKeys.Should().BeEquivalentTo("method", "api_key", "token", "api_sig", "format");
        sessionQuery["token"].Should().Be(token);
        sessionQuery["api_key"].Should().Be(key);
    }

    private void SetupValidCredentials()
    {
        _apiKeyService.GetApiKeyAsync("lastfm").Returns(Task.FromResult<string?>(ApiKey));
        _apiKeyService.GetApiKeyAsync("lastfm-secret").Returns(Task.FromResult<string?>(ApiSecret));
    }
    private void SetupHttpResponse(HttpStatusCode statusCode, object? content)
    {
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = JsonContent.Create(content)
        });
    }

}
