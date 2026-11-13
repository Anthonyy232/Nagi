using System.Net;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.OnlineProviders;
using Nagi.Core.OnlineProviders.Pipelines;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.OnlineProviders;

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
        _httpMessageHandler.Respond(HttpStatusCode.OK, responseContent);

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
        _httpMessageHandler.Respond(HttpStatusCode.OK, responseContent);

        await _metadataService.GetArtistInfoAsync(ArtistName, "de");

        _httpMessageHandler.Requests.Should().HaveCount(1);
        var query = HttpUtility.ParseQueryString(_httpMessageHandler.Requests[0].RequestUri!.Query);
        query["artist"].Should().Be(ArtistName);
        query["lang"].Should().Be("de");
    }

    [Fact]
    public async Task GetArtistInfoAsync_WithThreeLetterLanguageCode_OmitsLangParam()
    {
        SetupApiKey();
        _httpMessageHandler.Respond(HttpStatusCode.OK, CreateValidArtistResponse("Bio", "http://url.jpg", "http://url.jpg"));

        await _metadataService.GetArtistInfoAsync(ArtistName, "fil");

        HttpUtility.ParseQueryString(_httpMessageHandler.Requests[0].RequestUri!.Query)["lang"].Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 10)]
    [InlineData(HttpStatusCode.OK, 10)]
    [InlineData(HttpStatusCode.OK, 26)]
    public async Task GetArtistInfoAsync_WhenApiKeyIsInvalidOrSuspended_RefreshesKeyAndRetriesSuccessfully(HttpStatusCode statusCode,
        int errorCode)
    {
        SetupApiKey();
        _apiKeyService.RefreshApiKeyAsync("lastfm", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(RefreshedApiKey));

        var errorResponse = CreateErrorResponse(errorCode, "Invalid API Key");
        var successResponse = CreateValidArtistResponse("Bio", "http://url.jpg", "http://url.jpg");

        _httpMessageHandler.RespondSequence(
            (statusCode, JsonSerializer.Serialize(errorResponse)),
            (HttpStatusCode.OK, JsonSerializer.Serialize(successResponse)));

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
        _httpMessageHandler.Respond(HttpStatusCode.BadRequest, errorResponse);

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.IsConclusive.Should().BeFalse();
        _httpMessageHandler.Requests.Should().HaveCount(1);
    }
    [Theory]
    [InlineData("<a href=\"https://www.last.fm/music/Test\">Read more on Last.fm</a>")]
    [InlineData("   ")]
    public async Task GetArtistInfoAsync_WithoutBiographyOrRealImage_ReturnsNotFound(string summary)
    {
        SetupApiKey();
        _httpMessageHandler.Respond(HttpStatusCode.OK, new
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
        _httpMessageHandler.Respond(HttpStatusCode.OK, new { artist = (object?)null });

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
        result.Data.Should().BeNull();
    }

    // Last.fm sends error 6 with 200 for artists and with 404 for some other methods.
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetArtistInfoAsync_WhenArtistIsUnknown_ReturnsConclusiveNotFound(HttpStatusCode statusCode)
    {
        SetupApiKey();
        _httpMessageHandler.Respond(statusCode, CreateErrorResponse(6, "The artist you supplied could not be found"));

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }
    [Fact]
    public async Task GetArtistInfoAsync_WhenSuccessfulHttpResponseContainsApiError_ReturnsTemporaryError()
    {
        SetupApiKey();
        _httpMessageHandler.Respond(HttpStatusCode.OK, CreateErrorResponse(11, "Service unavailable"));

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }
    [Theory]
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
        _httpMessageHandler.Respond(HttpStatusCode.OK, responseContent);

        var result = await _metadataService.GetArtistInfoAsync(ArtistName);

        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data.Should().NotBeNull();
        result.Data!.ImageUrl.Should().Be(expected);
    }

    [Theory]
    [InlineData("OK Computer (Remastered)", "http://mega.jpg")]
    [InlineData("OK Computer OKNOTOK 1997 2017", null)]
    public async Task GetAlbumCoverUrlAsync_PrefersTheLargestRealImageOfTheRequestedAlbum(string returnedTitle, string? expected)
    {
        SetupApiKey();
        _httpMessageHandler.Respond(HttpStatusCode.OK, """
            {"album":{"name":"TITLE","artist":"Radiohead","image":[
              {"#text":"http://large.jpg","size":"large"},
              {"#text":"","size":"extralarge"},
              {"#text":"http://mega.jpg","size":"mega"}
            ]}}
            """.Replace("TITLE", returnedTitle));

        var result = await _metadataService.GetAlbumCoverUrlAsync("OK Computer", "Radiohead");

        result.Data.Should().Be(expected);
        _httpMessageHandler.Requests[0].RequestUri!.Query.Should()
            .Contain("method=album.getinfo").And.Contain("album=OK%20Computer").And.Contain("artist=Radiohead");
    }

    private void SetupApiKey(string key = ApiKey)
    {
        _apiKeyService.GetApiKeyAsync("lastfm", Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(key));
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
