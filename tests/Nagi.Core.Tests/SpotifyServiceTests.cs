using System.Net;
using System.Text;
using FluentAssertions;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Provides comprehensive unit tests for the <see cref="SpotifyService" />.
///     These tests verify the service's ability to manage access tokens, fetch artist images,
///     and gracefully handle various API errors, including rate limiting and credential issues.
///     All external dependencies are mocked to focus testing on the service's business logic.
/// </summary>
public class SpotifyServiceTests : IDisposable
{
    // Test data constants for consistency across tests.
    private const string ClientId = "test-client-id";
    private const string ClientSecret = "test-client-secret";
    private const string ArtistName = "Test Artist";
    private const string AccessToken = "BQD...and...so...on";

    private readonly IApiKeyService _apiKeyService;

    // Mocks for external dependencies, enabling isolated testing of the service's logic.
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    ///     A test message handler to mock HTTP responses for the HttpClient.
    /// </summary>
    private readonly TestHttpMessageHandler _httpMessageHandler;

    /// <summary>
    ///     The instance of the service under test.
    /// </summary>
    private readonly SpotifyService _service;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SpotifyServiceTests" /> class.
    ///     This constructor sets up the required mocks and instantiates the <see cref="SpotifyService" />
    ///     with these test dependencies.
    /// </summary>
    public SpotifyServiceTests()
    {
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _apiKeyService = Substitute.For<IApiKeyService>();
        _httpMessageHandler = new TestHttpMessageHandler();

        var httpClient = new HttpClient(_httpMessageHandler);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _service = new SpotifyService(_httpClientFactory, _apiKeyService);
    }

    /// <summary>
    ///     Performs application-defined tasks associated with freeing, releasing, or resetting
    ///     unmanaged resources. This method ensures test isolation by disposing the service and
    ///     other disposable resources after each test execution.
    /// </summary>
    public void Dispose()
    {
        _service.Dispose();
        _httpMessageHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    /// <summary>
    ///     Configures the mock <see cref="IApiKeyService" /> to return valid Spotify credentials.
    /// </summary>
    private void SetupValidApiKey()
    {
        _apiKeyService.GetApiKeyAsync("spotify", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>($"{ClientId}:{ClientSecret}"));
    }

    /// <summary>
    ///     Configures the mock <see cref="TestHttpMessageHandler" /> to return a specific HTTP response.
    /// </summary>
    private void SetupHttpResponse(HttpStatusCode statusCode, string? content = null)
    {
        var response = new HttpResponseMessage(statusCode);
        if (content != null) response.Content = new StringContent(content, Encoding.UTF8, "application/json");
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(response);
    }

    /// <summary>
    ///     Creates a JSON string for a successful Spotify token response.
    /// </summary>
    private static string CreateTokenResponseJson(string token, int expiresIn = 3600)
    {
        return $@"{{ ""access_token"": ""{token}"", ""token_type"": ""Bearer"", ""expires_in"": {expiresIn} }}";
    }

    /// <summary>
    ///     Creates a JSON string for a successful Spotify artist search response.
    /// </summary>
    private static string CreateArtistSearchResponseJson(string artistName, params (string url, int size)[] images)
    {
        var imageObjects =
            images.Select(i => $@"{{ ""url"": ""{i.url}"", ""height"": {i.size}, ""width"": {i.size} }}");
        var imagesJson = string.Join(",", imageObjects);
        return $@"
        {{
            ""artists"": {{
                ""items"": [
                    {{
                        ""name"": ""{artistName}"",
                        ""images"": [{imagesJson}]
                    }}
                ]
            }}
        }}";
    }

    #endregion

    #region GetArtistImageUrlAsync Tests

    /// <summary>
    ///     Verifies that <see cref="SpotifyService.GetArtistImageUrlAsync" /> successfully fetches an
    ///     access token, searches for an artist, and returns the URL of the largest available image.
    /// </summary>
    [Fact]
    public async Task GetArtistImageUrlAsync_WithValidArtist_ReturnsSuccessWithLargestImageUrl()
    {
        // Arrange
        SetupValidApiKey();
        var expectedImageUrl = "https://large.jpg";
        var tokenResponse = CreateTokenResponseJson(AccessToken);
        var searchResponse = CreateArtistSearchResponseJson(ArtistName,
            ("https://small.jpg", 160),
            (expectedImageUrl, 640), // Largest by area
            ("https://medium.jpg", 320));

        var responseQueue = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenResponse) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchResponse) }
        });
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(responseQueue.Dequeue());

        // Act
        var result = await _service.GetArtistImageUrlAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data.Should().NotBeNull();
        result.Data!.ImageUrl.Should().Be(expectedImageUrl);

        _httpMessageHandler.Requests.Should().HaveCount(2);
        _httpMessageHandler.Requests[0].RequestUri!.AbsoluteUri.Should().Be("https://accounts.spotify.com/api/token");
        _httpMessageHandler.Requests[1].RequestUri!.AbsoluteUri.Should().Contain("api.spotify.com/v1/search");
        _httpMessageHandler.Requests[1].Headers.Authorization!.Scheme.Should().Be("Bearer");
        _httpMessageHandler.Requests[1].Headers.Authorization!.Parameter.Should().Be(AccessToken);
    }

    /// <summary>
    ///     Verifies that <see cref="SpotifyService.GetArtistImageUrlAsync" /> returns a
    ///     <see cref="ServiceResultStatus.SuccessNotFound" /> status when the Spotify API finds
    ///     no matching artist.
    /// </summary>
    [Fact]
    public async Task GetArtistImageUrlAsync_WhenArtistNotFound_ReturnsSuccessNotFound()
    {
        // Arrange
        SetupValidApiKey();
        var tokenResponse = CreateTokenResponseJson(AccessToken);
        var searchResponse = @"{ ""artists"": { ""items"": [] } }"; // No artists found

        var responseQueue = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenResponse) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchResponse) }
        });
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(responseQueue.Dequeue());

        // Act
        var result = await _service.GetArtistImageUrlAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    /// <summary>
    ///     Verifies that <see cref="SpotifyService.GetArtistImageUrlAsync" /> returns a
    ///     <see cref="ServiceResultStatus.SuccessNotFound" /> status when a matching artist is
    ///     found but has no associated images.
    /// </summary>
    [Fact]
    public async Task GetArtistImageUrlAsync_WhenArtistHasNoImages_ReturnsSuccessNotFound()
    {
        // Arrange
        SetupValidApiKey();
        var tokenResponse = CreateTokenResponseJson(AccessToken);
        var searchResponse = CreateArtistSearchResponseJson(ArtistName); // No images provided

        var responseQueue = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenResponse) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchResponse) }
        });
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(responseQueue.Dequeue());

        // Act
        var result = await _service.GetArtistImageUrlAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    /// <summary>
    ///     Verifies that <see cref="SpotifyService.GetArtistImageUrlAsync" /> returns a
    ///     <see cref="ServiceResultStatus.PermanentError" /> for null or empty artist names
    ///     without making any network calls.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetArtistImageUrlAsync_WithInvalidArtistName_ReturnsPermanentError(string? artistName)
    {
        // Act
        var result = await _service.GetArtistImageUrlAsync(artistName!);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.PermanentError);
        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     Verifies that if the Spotify API returns a rate limit error (429), the service returns
    ///     a <see cref="ServiceResultStatus.PermanentError" /> and disables itself for the remainder
    ///     of the session to prevent further requests.
    /// </summary>
    [Fact]
    public async Task GetArtistImageUrlAsync_WhenRateLimited_DisablesApiAndReturnsPermanentError()
    {
        // Arrange
        SetupValidApiKey();
        var tokenResponse = CreateTokenResponseJson(AccessToken);

        var responseQueue = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenResponse) },
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        });
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(responseQueue.Dequeue());

        // Act
        var firstResult = await _service.GetArtistImageUrlAsync(ArtistName);
        _httpMessageHandler.Requests.Clear(); // Clear requests before the second call
        var secondResult = await _service.GetArtistImageUrlAsync(ArtistName);

        // Assert
        firstResult.Status.Should().Be(ServiceResultStatus.PermanentError);
        firstResult.ErrorMessage.Should().Contain("rate limit exceeded");

        secondResult.Status.Should().Be(ServiceResultStatus.PermanentError);
        secondResult.ErrorMessage.Should().Contain("disabled for this session");
        _httpMessageHandler.Requests.Should().BeEmpty(); // No HTTP call on the second attempt
    }

    /// <summary>
    ///     Verifies that <see cref="SpotifyService.GetArtistImageUrlAsync" /> returns a
    ///     <see cref="ServiceResultStatus.TemporaryError" /> if it fails to retrieve an access
    ///     token from the Spotify API.
    /// </summary>
    [Fact]
    public async Task GetArtistImageUrlAsync_WhenTokenFetchFails_ReturnsTemporaryError()
    {
        // Arrange
        SetupValidApiKey();
        SetupHttpResponse(HttpStatusCode.Unauthorized, @"{""error"":""invalid_client""}");

        // Act
        var result = await _service.GetArtistImageUrlAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
        result.ErrorMessage.Should().Contain("Could not retrieve Spotify access token");
        _httpMessageHandler.Requests.Should().HaveCount(1); // Only the token request should be made
    }

    /// <summary>
    ///     Verifies that <see cref="SpotifyService.GetArtistImageUrlAsync" /> returns a
    ///     <see cref="ServiceResultStatus.PermanentError" /> if the artist search API returns a
    ///     malformed JSON response that cannot be deserialized.
    /// </summary>
    [Fact]
    public async Task GetArtistImageUrlAsync_WhenSearchResponseIsMalformedJson_ReturnsPermanentError()
    {
        // Arrange
        SetupValidApiKey();
        var tokenResponse = CreateTokenResponseJson(AccessToken);

        var responseQueue = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenResponse) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ not valid json }") }
        });
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(responseQueue.Dequeue());

        // Act
        var result = await _service.GetArtistImageUrlAsync(ArtistName);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.PermanentError);
        result.ErrorMessage.Should().Contain("Failed to deserialize");
    }

    #endregion

    #region GetAccessTokenAsync Tests

    /// <summary>
    ///     Verifies that <see cref="SpotifyService.GetAccessTokenAsync" /> caches a valid access
    ///     token and returns the cached token on subsequent calls without making a new network request.
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_WithValidCachedToken_ReturnsTokenWithoutApiCall()
    {
        // Arrange
        SetupValidApiKey();
        SetupHttpResponse(HttpStatusCode.OK, CreateTokenResponseJson(AccessToken));

        // Act
        var firstToken = await _service.GetAccessTokenAsync(); // This will fetch and cache the token
        _httpMessageHandler.Requests.Clear(); // Clear requests before the second call
        var secondToken = await _service.GetAccessTokenAsync();

        // Assert
        firstToken.Should().Be(AccessToken);
        secondToken.Should().Be(AccessToken);
        _httpMessageHandler.Requests.Should().BeEmpty(); // No HTTP call was made for the second token
    }

    /// <summary>
    ///     Verifies that <see cref="SpotifyService.GetAccessTokenAsync" /> detects an expired
    ///     token and automatically fetches a new one from the API.
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_WithExpiredToken_FetchesNewToken()
    {
        // Arrange
        SetupValidApiKey();
        var firstTokenResponse = CreateTokenResponseJson("expired-token", 0);
        var secondTokenResponse = CreateTokenResponseJson("new-token", 3600);

        var responseQueue = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(firstTokenResponse) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(secondTokenResponse) }
        });
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(responseQueue.Dequeue());

        // Act
        var firstToken = await _service.GetAccessTokenAsync();
        var secondToken = await _service.GetAccessTokenAsync();

        // Assert
        firstToken.Should().Be("expired-token");
        secondToken.Should().Be("new-token");
        _httpMessageHandler.Requests.Should().HaveCount(2);
    }

    /// <summary>
    ///     Verifies that <see cref="SpotifyService.GetAccessTokenAsync" /> returns null and makes
    ///     no network calls if the Spotify API key is not configured.
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_WhenApiKeyIsMissing_ReturnsNullWithoutApiCall()
    {
        // Arrange
        _apiKeyService.GetApiKeyAsync("spotify", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        // Act
        var token = await _service.GetAccessTokenAsync();

        // Assert
        token.Should().BeNull();
        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    /// <summary>
    ///     Verifies that <see cref="SpotifyService.GetAccessTokenAsync" /> returns null and makes
    ///     no network calls if the configured Spotify API key is malformed (e.g., missing the
    ///     colon separator).
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_WhenApiKeyIsMalformed_ReturnsNullWithoutApiCall()
    {
        // Arrange
        _apiKeyService.GetApiKeyAsync("spotify", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("malformed-key-without-colon"));

        // Act
        var token = await _service.GetAccessTokenAsync();

        // Assert
        token.Should().BeNull();
        _httpMessageHandler.Requests.Should().BeEmpty();
    }

    #endregion
}