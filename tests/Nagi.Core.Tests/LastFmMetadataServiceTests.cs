using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
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
public class LastFmMetadataServiceTests : IDisposable
{
    private const string ApiKey = "test-api-key";
    private const string RefreshedApiKey = "refreshed-api-key";
    private const string ArtistName = "Test Artist";
    private readonly IApiKeyService _apiKeyService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TestHttpMessageHandler _httpMessageHandler;
    private readonly ILogger<LastFmMetadataService> _logger;
    private readonly LastFmMetadataService _metadataService;

    private readonly ProviderPipelineProvider _pipelines;

    public LastFmMetadataServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<LastFmMetadataService>>();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.LastFm);
        _metadataService = new LastFmMetadataService(_httpClientFactory, _pipelines, _apiKeyService, _logger);
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpMessageHandler.Dispose();
    }
    [Fact]
    public async Task GetArtistInfoAsync_WithValidResponse_ReturnsSuccessWithMappedData()
    {
        const string bio = "This is a summary. <a href=\"https://www.last.fm\">Read more</a>";
        var responseContent = CreateValidArtistResponse(bio, "http://extralarge.jpg", "http://large.jpg");
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data.Should().NotBeNull();
        result.Data!.Biography.Should().Be("This is a summary.");
        result.Data.ImageUrl.Should().Be("http://extralarge.jpg");

        _httpMessageHandler.Requests.Should().HaveCount(1);
        var query = HttpUtility.ParseQueryString(_httpMessageHandler.Requests[0].RequestUri!.Query);
        query["artist"].Should().Be(ArtistName);
    }

    [Fact]
    public async Task GetArtistInfoAsync_WithLanguageCode_IncludesLangParamInRequest()
    {
        var responseContent = CreateValidArtistResponse("Localized Bio", "http://url.jpg", "http://url.jpg");
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        await _metadataService.GetArtistInfoAsync(ArtistName, "de");

        _httpMessageHandler.Requests.Should().HaveCount(1);
        var query = HttpUtility.ParseQueryString(_httpMessageHandler.Requests[0].RequestUri!.Query);
        query["artist"].Should().Be(ArtistName);
        query["lang"].Should().Be("de");
    }
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.OK)]
    public async Task GetArtistInfoAsync_WhenApiKeyIsInvalid_RefreshesKeyAndRetriesSuccessfully(HttpStatusCode statusCode)
    {
        SetupApiKey();
        _apiKeyService.RefreshApiKeyAsync("lastfm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(RefreshedApiKey));

        var errorResponse = CreateErrorResponse(10, "Invalid API Key");
        var successResponse = CreateValidArtistResponse("Bio", "http://url.jpg", "http://url.jpg");

        var responseQueue = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(statusCode)
                { Content = new StringContent(JsonSerializer.Serialize(errorResponse)) },
            new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(successResponse)) }
        });
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(responseQueue.Dequeue());

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data.Should().NotBeNull();

        _httpMessageHandler.Requests.Should().HaveCount(2);
        _httpMessageHandler.Requests[0].RequestUri!.ToString().Should().Contain($"api_key={ApiKey}");
        _httpMessageHandler.Requests[1].RequestUri!.ToString().Should().Contain($"api_key={RefreshedApiKey}");
        await _apiKeyService.Received(1).RefreshApiKeyAsync("lastfm", Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task GetArtistInfoAsync_WhenApiKeyIsInvalidAndRefreshFails_RemainsRetryable()
    {
        SetupApiKey();
        _apiKeyService.RefreshApiKeyAsync("lastfm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var errorResponse = CreateErrorResponse(10, "Invalid API Key");
        SetupHttpResponse(HttpStatusCode.BadRequest, errorResponse);

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.IsConclusive.Should().BeFalse();
        _httpMessageHandler.Requests.Should().HaveCount(1);
    }
    [Fact]
    public async Task GetArtistInfoAsync_WhenInitialApiKeyIsMissing_RemainsRetryableWithoutHttpCall()
    {
        _apiKeyService.GetApiKeyAsync("lastfm", Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.IsConclusive.Should().BeFalse();
        _httpMessageHandler.Requests.Should().BeEmpty();

        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, CreateValidArtistResponse("Recovered bio", "http://image.jpg", "http://image.jpg"));

        var retry = await _metadataService.GetArtistInfoAsync(ArtistName);

        retry.Data!.Biography.Should().Be("Recovered bio");
        _httpMessageHandler.Requests.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("<a href=\"https://www.last.fm/music/Test\">Read more on Last.fm</a>")]
    [InlineData("   ")]
    public async Task GetArtistInfoAsync_WithoutBiographyOrRealImage_ReturnsNotFound(string summary)
    {
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, new
        {
            artist = new
            {
                bio = new { summary },
                image = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["size"] = "extralarge",
                        ["#text"] = "https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png"
                    }
                }
            }
        });

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }
    [Fact]
    public async Task GetArtistInfoAsync_WhenApiResponseIsNotFound_ReturnsSuccessNotFound()
    {
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, new { artist = (object?)null });

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
        result.Data.Should().BeNull();
    }
    [Fact]
    public async Task GetArtistInfoAsync_WhenApiReturnsOtherError_ReturnsTemporaryError()
    {
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.InternalServerError, null);

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(16)]
    public async Task GetArtistInfoAsync_WhenSuccessfulHttpResponseContainsApiError_ReturnsTemporaryError(int errorCode)
    {
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, CreateErrorResponse(errorCode, "Service unavailable"));

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }
    [Fact]
    public async Task GetArtistInfoAsync_WhenResponseIsMalformedJson_ReturnsTemporaryError()
    {
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, "{ not json }");

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }
    [Fact]
    public async Task GetArtistInfoAsync_WhenRequestIsCanceled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        SetupApiKey();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _httpMessageHandler.SendAsyncFunc = async (_, token) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var task = _metadataService.GetArtistInfoAsync(ArtistName, null, cts.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await task.Awaiting(t => t).Should().ThrowAsync<TaskCanceledException>();
    }
    [Theory]
    [InlineData("http://extralarge.jpg", "http://large.jpg", "http://extralarge.jpg")]
    [InlineData(null, "http://large.jpg", "http://large.jpg")]
    [InlineData(null, null, "http://small.jpg")]
    public async Task ToArtistInfo_SelectsBestImageUrlCorrectly(string? extraLarge, string? large, string expected)
    {
        var responseContent = new
        {
            artist = new
            {
                bio = new { summary = "A test bio." },
                image = new[]
                {
                    new Dictionary<string, object?> { { "size", "small" }, { "#text", "http://small.jpg" } },
                    new Dictionary<string, object?> { { "size", "large" }, { "#text", large } },
                    new Dictionary<string, object?> { { "size", "extralarge" }, { "#text", extraLarge } }
                }
            }
        };
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data.Should().NotBeNull();
        result.Data!.ImageUrl.Should().Be(expected);
    }

    private void SetupApiKey(string key = ApiKey)
    {
        _apiKeyService.GetApiKeyAsync("lastfm", Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(key));
    }
    private void SetupHttpResponse(HttpStatusCode statusCode, object? content)
    {
        var response = new HttpResponseMessage(statusCode);
        if (content != null)
        {
            var jsonContent = content is string s ? s : JsonSerializer.Serialize(content);
            response.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        }

        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(response);
    }
    private static object CreateValidArtistResponse(string summary, string extraLargeUrl, string largeUrl)
    {
        return new
        {
            artist = new
            {
                bio = new { summary },
                image = new[]
                {
                    new Dictionary<string, object> { { "size", "small" }, { "#text", "http://small.jpg" } },
                    new Dictionary<string, object> { { "size", "large" }, { "#text", largeUrl } },
                    new Dictionary<string, object> { { "size", "extralarge" }, { "#text", extraLargeUrl } }
                }
            }
        };
    }
    private static object CreateErrorResponse(int errorCode, string message)
    {
        return new { error = errorCode, message };
    }
}
