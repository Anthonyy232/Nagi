using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class ApiKeyServiceTests : IDisposable
{
    private readonly ApiKeyService _apiKeyService;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILogger<ApiKeyService> _logger;
    private readonly ProviderPipelineProvider _pipelines;

    public ApiKeyServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _configuration = Substitute.For<IConfiguration>();
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<ApiKeyService>>();

        // Must create a new HttpClient for each call - production code disposes HttpClient after use
        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(_httpMessageHandler, disposeHandler: false));

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.NagiApi);
        _apiKeyService = new ApiKeyService(_httpClientFactory, _pipelines, _configuration, _logger);
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpMessageHandler.Dispose();
    }

    [Fact]
    public async Task GetApiKeyAsync_FirstCall_FetchesKeyFromServerSuccessfully()
    {
        const string keyName = ServiceProviderIds.TheAudioDb;
        const string expectedApiKey = "12345-abcde";
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.OK, new { Value = expectedApiKey });

        var result = await _apiKeyService.GetApiKeyAsync(keyName);

        result.Should().Be(expectedApiKey);
        _httpMessageHandler.Requests.Should().HaveCount(1);
        var request = _httpMessageHandler.Requests[0];
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.ToString().Should().Be("https://api.test.com/api/theaudiodb-key");
        request.Headers.Contains("X-API-KEY").Should().BeTrue();
        request.Headers.Contains("Ocp-Apim-Subscription-Key").Should().BeTrue();
    }

    [Fact]
    public async Task RefreshApiKeyAsync_ForcesNewFetchFromServer_AndUpdatesCache()
    {
        const string keyName = ServiceProviderIds.TheAudioDb;
        const string initialApiKey = "initial-key";
        const string refreshedApiKey = "refreshed-key";
        SetupValidConfiguration();

        SetupHttpResponse(HttpStatusCode.OK, new { Value = initialApiKey });
        var result1 = await _apiKeyService.GetApiKeyAsync(keyName);

        SetupHttpResponse(HttpStatusCode.OK, new { Value = refreshedApiKey });
        var result2 = await _apiKeyService.RefreshApiKeyAsync(keyName);
        var result3 = await _apiKeyService.GetApiKeyAsync(keyName);

        result1.Should().Be(initialApiKey);
        result2.Should().Be(refreshedApiKey);
        result3.Should().Be(refreshedApiKey);
        _httpMessageHandler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetApiKeyAsync_AfterTransientFailure_RetriesOnNextCall()
    {
        const string expectedApiKey = "recovered-key";
        SetupValidConfiguration();
        _httpMessageHandler.RespondSequence(
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.OK, JsonSerializer.Serialize(new { Value = expectedApiKey })));

        var failedResult = await _apiKeyService.GetApiKeyAsync(ServiceProviderIds.LastFm);
        var recoveredResult = await _apiKeyService.GetApiKeyAsync(ServiceProviderIds.LastFm);

        failedResult.Should().BeNull();
        recoveredResult.Should().Be(expectedApiKey);
        _httpMessageHandler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetApiKeyAsync_WhenApiResponseValueIsInvalid_ReturnsNull(string? invalidValue)
    {
        const string keyName = ServiceProviderIds.FanartTv;
        SetupValidConfiguration();
        SetupHttpResponse(HttpStatusCode.OK, new { Value = invalidValue });

        var result = await _apiKeyService.GetApiKeyAsync(keyName);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenConfigurationIsMissing_ReturnsNullWithoutHttpCall()
    {
        _configuration["NagiApiServer:Url"].Returns(string.Empty);
        _configuration["NagiApiServer:ApiKey"].Returns("some-key");

        var result = await _apiKeyService.GetApiKeyAsync(ServiceProviderIds.TheAudioDb);

        result.Should().BeNull();
        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetApiKeyAsync_CancelingOneCaller_DoesNotCancelSharedFetch()
    {
        using var cts = new CancellationTokenSource();
        SetupValidConfiguration();

        var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _httpMessageHandler.SendAsyncFunc = (_, _) => tcs.Task;

        var apiTask = _apiKeyService.GetApiKeyAsync(ServiceProviderIds.TheAudioDb, cts.Token);
        var otherCaller = _apiKeyService.GetApiKeyAsync(ServiceProviderIds.TheAudioDb);
        cts.Cancel();

        await apiTask.Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>();
        tcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"Value\":\"shared-key\"}")
        });

        (await otherCaller).Should().Be("shared-key");
        (await _apiKeyService.GetApiKeyAsync(ServiceProviderIds.TheAudioDb)).Should().Be("shared-key");
        _httpMessageHandler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenHttpCallThrowsException_ReturnsNull()
    {
        SetupValidConfiguration();
        _httpMessageHandler.SendAsyncFunc = (_, _) => throw new HttpRequestException("Simulated network failure");

        var result = await _apiKeyService.GetApiKeyAsync(ServiceProviderIds.LastFm);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenHttpClientTimesOut_ReturnsNull()
    {
        SetupValidConfiguration();
        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(_httpMessageHandler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromMilliseconds(25)
            });
        _httpMessageHandler.SendAsyncFunc = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var result = await _apiKeyService.GetApiKeyAsync(ServiceProviderIds.LastFm);

        result.Should().BeNull();
        _httpMessageHandler.Requests.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("https://api.test.com/", "https://api.test.com/api/fanarttv-key")]
    [InlineData("api.test.com", "https://api.test.com/api/fanarttv-key")]
    [InlineData("HTTPS://api.test.com/", "https://api.test.com/api/fanarttv-key")]
    [InlineData("https://api.test.com/nagi?region=west", "https://api.test.com/nagi/api/fanarttv-key?region=west")]
    [InlineData("localhost:7443/nagi/", "https://localhost:7443/nagi/api/fanarttv-key")]
    public async Task GetApiKeyAsync_ConstructsRequestUrlCorrectly(string configuredUrl, string expectedRequestUrl)
    {
        SetupValidConfiguration(configuredUrl);
        SetupHttpResponse(HttpStatusCode.OK, new { Value = "some-key" });

        await _apiKeyService.GetApiKeyAsync(ServiceProviderIds.FanartTv);

        _httpMessageHandler.Requests.Should().HaveCount(1);
        var request = _httpMessageHandler.Requests[0];
        request.RequestUri!.ToString().Should().Be(expectedRequestUrl);
    }

    [Theory]
    [InlineData("sub-key", true)]
    [InlineData(null, false)]
    public async Task GetApiKeyAsync_HandlesSubscriptionKeyHeaderCorrectly(string? subscriptionKey,
        bool shouldHaveHeader)
    {
        SetupValidConfiguration(subscriptionKey: subscriptionKey);
        SetupHttpResponse(HttpStatusCode.OK, new { Value = "some-key" });

        await _apiKeyService.GetApiKeyAsync(ServiceProviderIds.TheAudioDb);

        _httpMessageHandler.Requests.Should().HaveCount(1);
        var request = _httpMessageHandler.Requests[0];
        var headerExists = request.Headers.Contains("Ocp-Apim-Subscription-Key");
        headerExists.Should().Be(shouldHaveHeader);
    }

    private void SetupValidConfiguration(string url = "https://api.test.com/", string? subscriptionKey = "sub-key")
    {
        _configuration["NagiApiServer:Url"].Returns(url);
        _configuration["NagiApiServer:ApiKey"].Returns("server-api-key");
        _configuration["NagiApiServer:SubscriptionKey"].Returns(subscriptionKey);
    }

    private void SetupHttpResponse(HttpStatusCode statusCode, object? content = null)
    {
        // Each request owns and disposes its response.
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

}
