using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Contains unit tests for the <see cref="ApiKeyService" />.
///     These tests verify the service's functionality related to fetching, caching, refreshing,
///     and handling errors for API keys from a remote server.
/// </summary>
public class ApiKeyServiceTests : IDisposable
{
    private readonly ApiKeyService _apiKeyService;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILogger<ApiKeyService> _logger;

    public ApiKeyServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _configuration = Substitute.For<IConfiguration>();
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<ApiKeyService>>();

        // Must create a new HttpClient for each call - production code disposes HttpClient after use
        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(_httpMessageHandler));

        _apiKeyService = new ApiKeyService(_httpClientFactory, _configuration, _logger);
    }

    public void Dispose()
    {
        _apiKeyService.Dispose();
        _httpMessageHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Verifies that the first call to <see cref="ApiKeyService.GetApiKeyAsync" /> for a given key
    ///     successfully fetches it from the remote server.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The API key cache is empty.
    ///     <br />
    ///     <b>Expected Result:</b> A single HTTP GET request is made to the correct endpoint with the
    ///     required `X-API-KEY` and `Ocp-Apim-Subscription-Key` headers, and the method returns the key
    ///     from the server's response.
    /// </remarks>
    [Fact]
    public async Task GetApiKeyAsync_FirstCall_FetchesKeyFromServerSuccessfully()
    {
        // Arrange
        const string keyName = ApiKeyServices.TheAudioDb;
        const string expectedApiKey = "12345-abcde";
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.OK, new { Value = expectedApiKey });

        // Act
        var result = await _apiKeyService.GetApiKeyAsync(keyName);

        // Assert
        result.Should().Be(expectedApiKey);
        _httpMessageHandler.Requests.Should().HaveCount(1);
        var request = _httpMessageHandler.Requests[0];
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.ToString().Should().Be("https://api.test.com/api/theaudiodb-key");
        request.Headers.Contains("X-API-KEY").Should().BeTrue();
        request.Headers.Contains("Ocp-Apim-Subscription-Key").Should().BeTrue();
    }

    /// <summary>
    ///     Verifies that a subsequent call to <see cref="ApiKeyService.GetApiKeyAsync" /> for the same key
    ///     returns the cached value without making a new HTTP request.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> An API key has already been fetched and is present in the cache.
    ///     <br />
    ///     <b>Expected Result:</b> The method returns the correct API key instantly without initiating
    ///     any new network requests, demonstrating effective caching.
    /// </remarks>
    [Fact]
    public async Task GetApiKeyAsync_SecondCallForSameKey_ReturnsFromCacheWithoutHttpCall()
    {
        // Arrange
        const string keyName = ApiKeyServices.TheAudioDb;
        const string expectedApiKey = "12345-abcde";
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.OK, new { Value = expectedApiKey });

        // Act
        var result1 = await _apiKeyService.GetApiKeyAsync(keyName);
        var result2 = await _apiKeyService.GetApiKeyAsync(keyName);

        // Assert
        result1.Should().Be(expectedApiKey);
        result2.Should().Be(expectedApiKey);
        _httpMessageHandler.Requests.Should().HaveCount(1);
    }

    /// <summary>
    ///     Verifies that <see cref="ApiKeyService.RefreshApiKeyAsync" /> forces a new API call to fetch an
    ///     updated key and updates the internal cache with the new value.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> An initial key is fetched and cached. `RefreshApiKeyAsync` is then called,
    ///     and the server is configured to return a different key.
    ///     <br />
    ///     <b>Expected Result:</b> Two HTTP requests are made in total. The first call returns the initial key,
    ///     the refresh call returns the new key, and a subsequent `GetApiKeyAsync` call also returns the
    ///     new, refreshed key from the updated cache.
    /// </remarks>
    [Fact]
    public async Task RefreshApiKeyAsync_ForcesNewFetchFromServer_AndUpdatesCache()
    {
        // Arrange
        const string keyName = ApiKeyServices.Spotify;
        const string initialApiKey = "initial-key";
        const string refreshedApiKey = "refreshed-key";
        SetupValidConfiguration();

        // Act
        SetupHttpResponse(HttpStatusCode.OK, new { Value = initialApiKey });
        var result1 = await _apiKeyService.GetApiKeyAsync(keyName);

        SetupHttpResponse(HttpStatusCode.OK, new { Value = refreshedApiKey });
        var result2 = await _apiKeyService.RefreshApiKeyAsync(keyName);
        var result3 = await _apiKeyService.GetApiKeyAsync(keyName);

        // Assert
        result1.Should().Be(initialApiKey);
        result2.Should().Be(refreshedApiKey);
        result3.Should().Be(refreshedApiKey);
        _httpMessageHandler.Requests.Should().HaveCount(2);
    }

    /// <summary>
    ///     Verifies that <see cref="ApiKeyService.GetApiKeyAsync" /> returns null when the underlying
    ///     HTTP request fails (e.g., returns a non-success status code).
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_WhenHttpCallFails_ReturnsNull()
    {
        // Arrange
        const string keyName = ApiKeyServices.LastFm;
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.InternalServerError);

        // Act
        var result = await _apiKeyService.GetApiKeyAsync(keyName);

        // Assert
        result.Should().BeNull();
        _httpMessageHandler.Requests.Should().HaveCount(1);
    }


    /// <summary>
    ///     Verifies that <see cref="ApiKeyService.GetApiKeyAsync" /> returns null when the API returns a
    ///     successful response but the JSON payload contains a null or whitespace value for the key.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetApiKeyAsync_WhenApiResponseValueIsInvalid_ReturnsNull(string? invalidValue)
    {
        // Arrange
        const string keyName = ApiKeyServices.FanartTv;
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.OK, new { Value = invalidValue });

        // Act
        var result = await _apiKeyService.GetApiKeyAsync(keyName);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that <see cref="ApiKeyService.GetApiKeyAsync" /> returns null when the API response
    ///     body is not in the expected JSON format.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_WhenApiResponseIsMalformedJson_ReturnsNull()
    {
        // Arrange
        const string keyName = ApiKeyServices.LastFmSecret;
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.OK, new { Message = "This is not the expected format" });

        // Act
        var result = await _apiKeyService.GetApiKeyAsync(keyName);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that <see cref="ApiKeyService.GetApiKeyAsync" /> returns null without making an HTTP
    ///     call if the required API server configuration (URL or key) is missing.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_WhenConfigurationIsMissing_ReturnsNullWithoutHttpCall()
    {
        // Arrange
        _configuration["NagiApiServer:Url"].Returns(string.Empty);
        _configuration["NagiApiServer:ApiKey"].Returns("some-key");

        // Act
        var result = await _apiKeyService.GetApiKeyAsync(ApiKeyServices.TheAudioDb);

        // Assert
        result.Should().BeNull();
        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     Verifies that <see cref="ApiKeyService.GetApiKeyAsync" /> throws an <see cref="OperationCanceledException" />
    ///     when the provided cancellation token is triggered during the request.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_WhenRequestIsCanceled_ThrowsOperationCanceledException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        SetupValidConfiguration();

        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        cts.Token.Register(() => tcs.TrySetCanceled());
        _httpMessageHandler.SendAsyncFunc = (_, _) => tcs.Task;

        // Act
        var apiTask = _apiKeyService.GetApiKeyAsync(ApiKeyServices.Spotify, cts.Token);
        cts.Cancel();

        // Assert
        await apiTask.Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    ///     Verifies that <see cref="ApiKeyService.GetApiKeyAsync" /> returns null when the underlying
    ///     <see cref="HttpClient" /> call throws an <see cref="HttpRequestException" />.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_WhenHttpCallThrowsException_ReturnsNull()
    {
        // Arrange
        SetupValidConfiguration();
        _httpMessageHandler.SendAsyncFunc = (_, _) => throw new HttpRequestException("Simulated network failure");

        // Act
        var result = await _apiKeyService.GetApiKeyAsync(ApiKeyServices.LastFm);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that <see cref="ApiKeyService.GetApiKeyAsync" /> correctly constructs the final request
    ///     URL from various formats of the base URL provided in the configuration.
    /// </summary>
    [Theory]
    [InlineData("https://api.test.com/", "https://api.test.com/api/fanarttv-key")]
    [InlineData("https://api.test.com", "https://api.test.com/api/fanarttv-key")]
    [InlineData("api.test.com", "https://api.test.com/api/fanarttv-key")]
    public async Task GetApiKeyAsync_ConstructsRequestUrlCorrectly(string configuredUrl, string expectedRequestUrl)
    {
        // Arrange
        SetupValidConfiguration(configuredUrl);
        SetupHttpResponse(HttpStatusCode.OK, new { Value = "some-key" });

        // Act
        await _apiKeyService.GetApiKeyAsync(ApiKeyServices.FanartTv);

        // Assert
        _httpMessageHandler.Requests.Should().HaveCount(1);
        var request = _httpMessageHandler.Requests[0];
        request.RequestUri!.ToString().Should().Be(expectedRequestUrl);
    }

    /// <summary>
    ///     Verifies that the `Ocp-Apim-Subscription-Key` header is added to the request if and only if
    ///     a subscription key is present in the configuration.
    /// </summary>
    [Theory]
    [InlineData("sub-key", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public async Task GetApiKeyAsync_HandlesSubscriptionKeyHeaderCorrectly(string? subscriptionKey,
        bool shouldHaveHeader)
    {
        // Arrange
        SetupValidConfiguration(subscriptionKey: subscriptionKey);
        SetupHttpResponse(HttpStatusCode.OK, new { Value = "some-key" });

        // Act
        await _apiKeyService.GetApiKeyAsync(ApiKeyServices.TheAudioDb);

        // Assert
        _httpMessageHandler.Requests.Should().HaveCount(1);
        var request = _httpMessageHandler.Requests[0];
        var headerExists = request.Headers.Contains("Ocp-Apim-Subscription-Key");
        headerExists.Should().Be(shouldHaveHeader);
    }

    /// <summary>
    ///     Verifies that multiple concurrent calls to <see cref="ApiKeyService.GetApiKeyAsync" /> for the
    ///     same key result in only one underlying HTTP request.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> Three tasks are started concurrently to fetch the same API key. The HTTP response
    ///     is delayed to ensure they all execute before the first one completes.
    ///     <br />
    ///     <b>Expected Result:</b> Only one network request is sent. All three tasks complete successfully
    ///     and return the same, correct API key, demonstrating protection against race conditions.
    /// </remarks>
    [Fact]
    public async Task GetApiKeyAsync_ConcurrentCallsForSameKey_ResultInSingleHttpCall()
    {
        // Arrange
        const string keyName = ApiKeyServices.TheAudioDb;
        const string expectedApiKey = "shared-key";
        SetupValidConfiguration();

        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { Value = expectedApiKey }), Encoding.UTF8,
                "application/json")
        };
        _httpMessageHandler.SendAsyncFunc = (_, _) => tcs.Task;

        // Act
        var task1 = _apiKeyService.GetApiKeyAsync(keyName);
        var task2 = _apiKeyService.GetApiKeyAsync(keyName);
        var task3 = _apiKeyService.GetApiKeyAsync(keyName);

        tcs.SetResult(response);
        var results = await Task.WhenAll(task1, task2, task3);

        // Assert
        _httpMessageHandler.Requests.Should().HaveCount(1);
        results.Should().AllBe(expectedApiKey);
    }

    #region Helper Methods

    /// <summary>
    ///     Configures the mock <see cref="IConfiguration" /> with valid API server settings.
    /// </summary>
    private void SetupValidConfiguration(string url = "https://api.test.com/", string? subscriptionKey = "sub-key")
    {
        _configuration["NagiApiServer:Url"].Returns(url);
        _configuration["NagiApiServer:ApiKey"].Returns("server-api-key");
        _configuration["NagiApiServer:SubscriptionKey"].Returns(subscriptionKey);
    }

    /// <summary>
    ///     Configures the mock <see cref="TestHttpMessageHandler" /> to return a specific <see cref="HttpResponseMessage" />.
    ///     Creates a new response for each request to avoid content stream reuse issues.
    /// </summary>
    private void SetupHttpResponse(HttpStatusCode statusCode, object? content = null)
    {
        // Must create a new HttpResponseMessage for each request - content stream can only be read once
        _httpMessageHandler.SendAsyncFunc = (_, _) =>
        {
            var response = new HttpResponseMessage(statusCode);
            if (content != null)
            {
                var jsonContent = JsonSerializer.Serialize(content);
                response.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        };
    }

    #endregion
}