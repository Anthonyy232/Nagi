using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Nagi.Core.Services.Implementations;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
/// Provides a test-specific HttpMessageHandler that intercepts outgoing HTTP requests,
/// records them, and returns a configurable mock response.
/// </summary>
public class TestHttpMessageHandler : HttpMessageHandler {
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendAsyncFunc { get; set; }
    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        Requests.Add(request);
        return SendAsyncFunc != null
            ? SendAsyncFunc(request, cancellationToken)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

/// <summary>
/// Contains unit tests for the <see cref="ApiKeyService"/>.
/// </summary>
public class ApiKeyServiceTests : IDisposable {
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ApiKeyService _apiKeyService;

    public ApiKeyServiceTests() {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _configuration = Substitute.For<IConfiguration>();
        _httpMessageHandler = new TestHttpMessageHandler();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _apiKeyService = new ApiKeyService(_httpClientFactory, _configuration);
    }

    public void Dispose() {
        _apiKeyService.Dispose();
        _httpMessageHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    /// <summary>
    /// Configures the IConfiguration mock to return valid API server settings.
    /// </summary>
    private void SetupValidConfiguration(string url = "https://api.test.com/", string? subscriptionKey = "sub-key") {
        _configuration["NagiApiServer:Url"].Returns(url);
        _configuration["NagiApiServer:ApiKey"].Returns("server-api-key");
        _configuration["NagiApiServer:SubscriptionKey"].Returns(subscriptionKey);
    }

    /// <summary>
    /// Configures the mock HttpMessageHandler to return a response with the specified status code and content.
    /// </summary>
    private void SetupHttpResponse(HttpStatusCode statusCode, object? content = null) {
        var response = new HttpResponseMessage(statusCode);
        if (content != null) {
            var jsonContent = JsonSerializer.Serialize(content);
            response.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        }

        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(response);
    }

    #endregion

    /// <summary>
    /// Verifies that the first request for an API key triggers an HTTP call to the server and returns the fetched key.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_FirstCall_FetchesKeyFromServerSuccessfully() {
        const string keyName = "testKey";
        const string expectedApiKey = "12345-abcde";
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.OK, new { Value = expectedApiKey });

        var result = await _apiKeyService.GetApiKeyAsync(keyName);

        Assert.Equal(expectedApiKey, result);
        Assert.Single(_httpMessageHandler.Requests);
        var request = _httpMessageHandler.Requests[0];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"https://api.test.com/api/{keyName}-key", request.RequestUri!.ToString());
        Assert.True(request.Headers.Contains("X-API-KEY"));
        Assert.True(request.Headers.Contains("Ocp-Apim-Subscription-Key"));
    }

    /// <summary>
    /// Verifies that subsequent requests for the same API key are served from the cache without making additional HTTP calls.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_SecondCallForSameKey_ReturnsFromCacheWithoutHttpCall() {
        const string keyName = "testKey";
        const string expectedApiKey = "12345-abcde";
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.OK, new { Value = expectedApiKey });

        var result1 = await _apiKeyService.GetApiKeyAsync(keyName);
        var result2 = await _apiKeyService.GetApiKeyAsync(keyName);

        Assert.Equal(expectedApiKey, result1);
        Assert.Equal(expectedApiKey, result2);
        Assert.Single(_httpMessageHandler.Requests);
    }

    /// <summary>
    /// Verifies that RefreshApiKeyAsync forces a new HTTP call for a key and updates the cached value.
    /// </summary>
    [Fact]
    public async Task RefreshApiKeyAsync_ForcesNewFetchFromServer_AndUpdatesCache() {
        const string keyName = "testKey";
        const string initialApiKey = "initial-key";
        const string refreshedApiKey = "refreshed-key";
        SetupValidConfiguration();

        SetupHttpResponse(HttpStatusCode.OK, new { Value = initialApiKey });
        var result1 = await _apiKeyService.GetApiKeyAsync(keyName);

        SetupHttpResponse(HttpStatusCode.OK, new { Value = refreshedApiKey });
        var result2 = await _apiKeyService.RefreshApiKeyAsync(keyName);
        var result3 = await _apiKeyService.GetApiKeyAsync(keyName);

        Assert.Equal(initialApiKey, result1);
        Assert.Equal(refreshedApiKey, result2);
        Assert.Equal(refreshedApiKey, result3);
        Assert.Equal(2, _httpMessageHandler.Requests.Count);
    }

    /// <summary>
    /// Verifies that GetApiKeyAsync returns null when the underlying HTTP request fails with a server error.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_WhenHttpCallFails_ReturnsNull() {
        const string keyName = "errorKey";
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.InternalServerError);

        var result = await _apiKeyService.GetApiKeyAsync(keyName);

        Assert.Null(result);
        Assert.Single(_httpMessageHandler.Requests);
    }

    /// <summary>
    /// Verifies that GetApiKeyAsync returns null when the server provides a successful response but the key value is null or whitespace.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetApiKeyAsync_WhenApiResponseValueIsInvalid_ReturnsNull(string? invalidValue) {
        const string keyName = "badValueKey";
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.OK, new { Value = invalidValue });

        var result = await _apiKeyService.GetApiKeyAsync(keyName);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that GetApiKeyAsync returns null when the server response is not in the expected JSON format.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_WhenApiResponseIsMalformedJson_ReturnsNull() {
        const string keyName = "badJsonKey";
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.OK, new { Message = "This is not the expected format" });

        var result = await _apiKeyService.GetApiKeyAsync(keyName);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that GetApiKeyAsync returns null and makes no HTTP call if the required server configuration is missing.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_WhenConfigurationIsMissing_ReturnsNullWithoutHttpCall() {
        _configuration["NagiApiServer:Url"].Returns(string.Empty);
        _configuration["NagiApiServer:ApiKey"].Returns("some-key");

        var result = await _apiKeyService.GetApiKeyAsync("anyKey");

        Assert.Null(result);
        Assert.Empty(_httpMessageHandler.Requests);
    }

    /// <summary>
    /// Verifies that GetApiKeyAsync properly propagates an OperationCanceledException when the cancellation token is triggered during an HTTP request.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_WhenRequestIsCanceled_ThrowsOperationCanceledException() {
        var cts = new CancellationTokenSource();
        SetupValidConfiguration();

        // Use a TaskCompletionSource to simulate a long-running request that can be externally controlled.
        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        cts.Token.Register(() => tcs.TrySetCanceled());
        _httpMessageHandler.SendAsyncFunc = (_, _) => tcs.Task;

        var apiTask = _apiKeyService.GetApiKeyAsync("cancelKey", cts.Token);

        // Trigger cancellation, which causes the awaited tcs.Task to throw.
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apiTask);
    }

    /// <summary>
    /// Verifies that GetApiKeyAsync returns null if the HttpClient throws an HttpRequestException during the request.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_WhenHttpCallThrowsException_ReturnsNull() {
        SetupValidConfiguration();
        _httpMessageHandler.SendAsyncFunc = (_, _) => throw new HttpRequestException("Simulated network failure");

        var result = await _apiKeyService.GetApiKeyAsync("exceptionKey");

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that the request URL is constructed correctly based on various formats of the configured base URL.
    /// </summary>
    [Theory]
    [InlineData("https://api.test.com/", "https://api.test.com/api/myKey-key")]
    [InlineData("https://api.test.com", "https://api.test.com/api/myKey-key")]
    [InlineData("api.test.com", "https://api.test.com/api/myKey-key")]
    public async Task GetApiKeyAsync_ConstructsRequestUrlCorrectly(string configuredUrl, string expectedRequestUrl) {
        SetupValidConfiguration(url: configuredUrl);
        SetupHttpResponse(HttpStatusCode.OK, new { Value = "some-key" });

        await _apiKeyService.GetApiKeyAsync("myKey");

        Assert.Single(_httpMessageHandler.Requests);
        var request = _httpMessageHandler.Requests[0];
        Assert.Equal(expectedRequestUrl, request.RequestUri!.ToString());
    }

    /// <summary>
    /// Verifies that the Ocp-Apim-Subscription-Key header is correctly added to requests only when a subscription key is configured.
    /// </summary>
    [Theory]
    [InlineData("sub-key", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public async Task GetApiKeyAsync_HandlesSubscriptionKeyHeaderCorrectly(string? subscriptionKey, bool shouldHaveHeader) {
        SetupValidConfiguration(subscriptionKey: subscriptionKey);
        SetupHttpResponse(HttpStatusCode.OK, new { Value = "some-key" });

        await _apiKeyService.GetApiKeyAsync("testKey");

        Assert.Single(_httpMessageHandler.Requests);
        var request = _httpMessageHandler.Requests[0];
        bool headerExists = request.Headers.Contains("Ocp-Apim-Subscription-Key");
        Assert.Equal(shouldHaveHeader, headerExists);
    }

    /// <summary>
    /// Verifies that multiple concurrent requests for the same key result in only a single HTTP call, with all callers receiving the same result.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_ConcurrentCallsForSameKey_ResultInSingleHttpCall() {
        const string keyName = "concurrentKey";
        const string expectedApiKey = "shared-key";
        SetupValidConfiguration();

        // Use a TaskCompletionSource to control response delivery, ensuring all tasks are awaiting before the response is sent.
        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        var response = new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(JsonSerializer.Serialize(new { Value = expectedApiKey }), Encoding.UTF8, "application/json")
        };
        _httpMessageHandler.SendAsyncFunc = (_, _) => tcs.Task;

        var task1 = _apiKeyService.GetApiKeyAsync(keyName);
        var task2 = _apiKeyService.GetApiKeyAsync(keyName);
        var task3 = _apiKeyService.GetApiKeyAsync(keyName);

        // Release the response, allowing all awaiting tasks to complete.
        tcs.SetResult(response);

        var results = await Task.WhenAll(task1, task2, task3);

        Assert.Single(_httpMessageHandler.Requests);
        Assert.All(results, result => Assert.Equal(expectedApiKey, result));
    }
}