using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Contains unit tests for the <see cref="LastFmMetadataService" />.
///     These tests verify the service's ability to fetch artist information, handle API errors
///     (including invalid API keys and retry logic), and correctly parse various API responses.
/// </summary>
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

    public LastFmMetadataServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _httpMessageHandler = new TestHttpMessageHandler();
        _logger = Substitute.For<ILogger<LastFmMetadataService>>();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _metadataService = new LastFmMetadataService(_httpClientFactory, _apiKeyService, _logger);
    }

    public void Dispose()
    {
        _httpMessageHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Verifies that a successful API response for an artist is correctly parsed and mapped to an
    ///     <see cref="ArtistInfo" /> object.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The Last.fm API returns a 200 OK response with valid artist data.
    ///     <br />
    ///     <b>Expected Result:</b> The service returns a `Success` result. The biography HTML is sanitized,
    ///     and the highest-resolution image URL ('extralarge') is selected.
    /// </remarks>
    [Fact]
    public async Task GetArtistInfoAsync_WithValidResponse_ReturnsSuccessWithMappedData()
    {
        // Arrange
        const string bio = "This is a summary. <a href=\"https://www.last.fm\">Read more</a>";
        var responseContent = CreateValidArtistResponse(bio, "http://extralarge.jpg", "http://large.jpg");
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        // Assert
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
        // Arrange
        var responseContent = CreateValidArtistResponse("Localized Bio", "http://url.jpg", "http://url.jpg");
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        await _metadataService.GetArtistInfoAsync(ArtistName, "de");

        // Assert
        _httpMessageHandler.Requests.Should().HaveCount(1);
        var query = HttpUtility.ParseQueryString(_httpMessageHandler.Requests[0].RequestUri!.Query);
        query["artist"].Should().Be(ArtistName);
        query["lang"].Should().Be("de");
    }

    /// <summary>
    ///     Verifies the retry logic for an invalid API key. Ensures that the service attempts to refresh
    ///     the key and successfully retries the request with the new key.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The first API call fails with an "Invalid API Key" error. The API key service
    ///     successfully provides a new, valid key upon refresh.
    ///     <br />
    ///     <b>Expected Result:</b> The service makes two HTTP requests. The first uses the old key and fails.
    ///     The second uses the new, refreshed key and succeeds. The final result is `Success`.
    /// </remarks>
    [Fact]
    public async Task GetArtistInfoAsync_WhenApiKeyIsInvalid_RefreshesKeyAndRetriesSuccessfully()
    {
        // Arrange
        SetupApiKey();
        _apiKeyService.RefreshApiKeyAsync("lastfm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(RefreshedApiKey));

        var errorResponse = CreateErrorResponse(10, "Invalid API Key");
        var successResponse = CreateValidArtistResponse("Bio", "http://url.jpg", "http://url.jpg");

        var responseQueue = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent(JsonSerializer.Serialize(errorResponse)) },
            new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(successResponse)) }
        });
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(responseQueue.Dequeue());

        // Act
        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data.Should().NotBeNull();

        _httpMessageHandler.Requests.Should().HaveCount(2);
        _httpMessageHandler.Requests[0].RequestUri!.ToString().Should().Contain($"api_key={ApiKey}");
        _httpMessageHandler.Requests[1].RequestUri!.ToString().Should().Contain($"api_key={RefreshedApiKey}");
        await _apiKeyService.Received(1).RefreshApiKeyAsync("lastfm", Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Verifies that if the initial API key is invalid and the subsequent refresh attempt also fails,
    ///     the service returns a <see cref="ServiceResultStatus.PermanentError" /> without further retries.
    /// </summary>
    [Fact]
    public async Task GetArtistInfoAsync_WhenApiKeyIsInvalidAndRefreshFails_ReturnsPermanentError()
    {
        // Arrange
        SetupApiKey();
        _apiKeyService.RefreshApiKeyAsync("lastfm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var errorResponse = CreateErrorResponse(10, "Invalid API Key");
        SetupHttpResponse(HttpStatusCode.BadRequest, errorResponse);

        // Act
        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.PermanentError);
        _httpMessageHandler.Requests.Should().HaveCount(1);
    }

    /// <summary>
    ///     Verifies that if the initial API key is missing, the service returns a
    ///     <see cref="ServiceResultStatus.PermanentError" /> without attempting an HTTP request.
    /// </summary>
    [Fact]
    public async Task GetArtistInfoAsync_WhenInitialApiKeyIsMissing_ReturnsPermanentErrorWithoutHttpCall()
    {
        // Arrange
        _apiKeyService.GetApiKeyAsync("lastfm", Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));

        // Act
        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.PermanentError);
        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     Verifies that when the Last.fm API response indicates the artist was not found, the service
    ///     returns a <see cref="ServiceResultStatus.SuccessNotFound" /> result.
    /// </summary>
    [Fact]
    public async Task GetArtistInfoAsync_WhenApiResponseIsNotFound_ReturnsSuccessNotFound()
    {
        // Arrange
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, new { artist = (object?)null });

        // Act
        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
        result.Data.Should().BeNull();
    }

    /// <summary>
    ///     Verifies that a server-side error (HTTP 5xx) from the Last.fm API results in a
    ///     <see cref="ServiceResultStatus.TemporaryError" />.
    /// </summary>
    [Fact]
    public async Task GetArtistInfoAsync_WhenApiReturnsOtherError_ReturnsTemporaryError()
    {
        // Arrange
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.InternalServerError, null);

        // Act
        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    /// <summary>
    ///     Verifies that a malformed JSON response from the Last.fm API results in a
    ///     <see cref="ServiceResultStatus.PermanentError" />.
    /// </summary>
    [Fact]
    public async Task GetArtistInfoAsync_WhenResponseIsMalformedJson_ReturnsPermanentError()
    {
        // Arrange
        SetupApiKey();
        SetupHttpResponse(HttpStatusCode.OK, "{ not json }");

        // Act
        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.PermanentError);
    }

    /// <summary>
    ///     Verifies that if the request is canceled via a <see cref="CancellationToken" />, the service
    ///     propagates an <see cref="OperationCanceledException" />.
    /// </summary>
    [Fact]
    public async Task GetArtistInfoAsync_WhenRequestIsCanceled_ThrowsOperationCanceledException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        SetupApiKey();
        _httpMessageHandler.SendAsyncFunc = async (_, token) =>
        {
            await Task.Delay(1000, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        // Act
        var task = _metadataService.GetArtistInfoAsync(ArtistName, null, cts.Token);
        cts.Cancel();

        // Assert
        await task.Awaiting(t => t).Should().ThrowAsync<TaskCanceledException>();
    }

    /// <summary>
    ///     Verifies the image selection logic correctly chooses the best available image URL, prioritizing
    ///     'extralarge', then 'large', and falling back to other sizes.
    /// </summary>
    [Theory]
    [InlineData("http://extralarge.jpg", "http://large.jpg", "http://extralarge.jpg")]
    [InlineData(null, "http://large.jpg", "http://large.jpg")]
    [InlineData(null, null, "http://small.jpg")] // Falls back to last available
    public async Task ToArtistInfo_SelectsBestImageUrlCorrectly(string? extraLarge, string? large, string expected)
    {
        // Arrange
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

        // Act
        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data.Should().NotBeNull();
        result.Data!.ImageUrl.Should().Be(expected);
    }

    #region Helper Methods

    /// <summary>
    ///     Configures the mock <see cref="IApiKeyService" /> to return a specific API key.
    /// </summary>
    private void SetupApiKey(string key = ApiKey)
    {
        _apiKeyService.GetApiKeyAsync("lastfm", Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(key));
    }

    /// <summary>
    ///     Configures the mock <see cref="TestHttpMessageHandler" /> to return a specific HTTP response.
    /// </summary>
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

    /// <summary>
    ///     Creates a mock Last.fm API response object for a successful artist.getInfo call.
    /// </summary>
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

    /// <summary>
    ///     Creates a mock Last.fm API error response object.
    /// </summary>
    private static object CreateErrorResponse(int errorCode, string message)
    {
        return new { error = errorCode, message };
    }

    #endregion
}