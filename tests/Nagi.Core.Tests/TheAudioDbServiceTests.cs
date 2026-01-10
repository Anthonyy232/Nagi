using System.Net;
using System.Text.Json;
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
///     Comprehensive tests for TheAudioDbService covering metadata fetching,
///     error handling, rate limiting, and cancellation.
/// </summary>
public class TheAudioDbServiceTests : IDisposable
{
    private const string ValidMbid = "cc197bad-dc9c-440d-a5b5-d52ba2e14234"; // Coldplay

    private readonly TestHttpMessageHandler _httpHandler;
    private readonly TheAudioDbService _service;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<TheAudioDbService> _logger;

    public TheAudioDbServiceTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://www.theaudiodb.com") };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _apiKeyService = Substitute.For<IApiKeyService>();
        _apiKeyService.GetApiKeyAsync("theaudiodb", Arg.Any<CancellationToken>()).Returns("123");

        _logger = Substitute.For<ILogger<TheAudioDbService>>();

        _service = new TheAudioDbService(httpClientFactory, _apiKeyService, _logger);
    }

    public void Dispose()
    {
        _service.Dispose();
        _httpHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Successful Fetch Tests

    [Fact]
    public async Task GetArtistMetadataAsync_WithValidMbid_ReturnsMetadata()
    {
        // Arrange
        var audioDbResponse = new
        {
            artists = new[]
            {
                new
                {
                    strBiographyEN = "A famous rock band from the UK.",
                    strArtistThumb = "https://theaudiodb.com/thumb.jpg",
                    strArtistFanart = "https://theaudiodb.com/fanart.jpg",
                    strArtistWideThumb = "https://theaudiodb.com/wide.jpg",
                    strArtistLogo = "https://theaudiodb.com/logo.png"
                }
            }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(audioDbResponse))
        });

        // Act
        var result = await _service.GetArtistMetadataAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data.Should().NotBeNull();
        result.Data!.Biography.Should().Be("A famous rock band from the UK.");
        result.Data.ThumbUrl.Should().Be("https://theaudiodb.com/thumb.jpg");
        result.Data.FanartUrl.Should().Be("https://theaudiodb.com/fanart.jpg");
        result.Data.WideThumbUrl.Should().Be("https://theaudiodb.com/wide.jpg");
        result.Data.LogoUrl.Should().Be("https://theaudiodb.com/logo.png");
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WithPartialData_ReturnsPartialResult()
    {
        // Arrange - Only biography, no images
        var audioDbResponse = new
        {
            artists = new[]
            {
                new
                {
                    strBiographyEN = "Bio only artist."
                }
            }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(audioDbResponse))
        });

        // Act
        var result = await _service.GetArtistMetadataAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data!.Biography.Should().Be("Bio only artist.");
        result.Data.ThumbUrl.Should().BeNull();
        result.Data.FanartUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetArtistMetadataAsync_ReturnsBothThumbAndFanartUrls()
    {
        // Arrange
        var audioDbResponse = new
        {
            artists = new[]
            {
                new
                {
                    strArtistThumb = "https://theaudiodb.com/thumb.jpg",
                    strArtistFanart = "https://theaudiodb.com/fanart.jpg"
                }
            }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(audioDbResponse))
        });

        // Act
        var result = await _service.GetArtistMetadataAsync(ValidMbid);

        // Assert - Both URLs should be available for the consumer to choose from
        result.Data!.ThumbUrl.Should().Be("https://theaudiodb.com/thumb.jpg");
        result.Data!.FanartUrl.Should().Be("https://theaudiodb.com/fanart.jpg");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetArtistMetadataAsync_When404_ReturnsSuccessNotFound()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        // Act
        var result = await _service.GetArtistMetadataAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WithEmptyMbid_ReturnsSuccessNotFound()
    {
        // Act
        var result = await _service.GetArtistMetadataAsync("");

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WithWhitespaceMbid_ReturnsSuccessNotFound()
    {
        // Act
        var result = await _service.GetArtistMetadataAsync("   ");

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WhenApiKeyMissing_ReturnsTemporaryError()
    {
        // Arrange
        _apiKeyService.GetApiKeyAsync("theaudiodb", Arg.Any<CancellationToken>()).Returns((string?)null);

        // Act
        var result = await _service.GetArtistMetadataAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WhenHttpError_ReturnsTemporaryError()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        // Act
        var result = await _service.GetArtistMetadataAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    #endregion

    #region Rate Limiting Tests

    [Fact]
    public async Task GetArtistMetadataAsync_When429RateLimited_DisablesServiceForSession()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        // Act - First call triggers rate limit
        var result1 = await _service.GetArtistMetadataAsync(ValidMbid);

        // Reset handler to return success
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"artists\": [{\"strBiographyEN\": \"Bio\"}]}")
        });

        // Act - Second call should be short-circuited
        var result2 = await _service.GetArtistMetadataAsync(ValidMbid);

        // Assert
        result1.Status.Should().Be(ServiceResultStatus.PermanentError);
        result2.Status.Should().Be(ServiceResultStatus.PermanentError);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task GetArtistMetadataAsync_WhenCancelled_ThrowsOperationCancelled()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.GetArtistMetadataAsync(ValidMbid, cts.Token));
    }

    #endregion

    #region Empty Response Handling

    [Fact]
    public async Task GetArtistMetadataAsync_WithEmptyArtistData_ReturnsSuccessNotFound()
    {
        // Arrange - Response with artist but all null fields
        var audioDbResponse = new
        {
            artists = new[]
            {
                new
                {
                    strBiographyEN = (string?)null,
                    strArtistThumb = (string?)null
                }
            }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(audioDbResponse))
        });

        // Act
        var result = await _service.GetArtistMetadataAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WithNullArtistsArray_ReturnsSuccessNotFound()
    {
        // Arrange
        var audioDbResponse = new { artists = (object[]?)null };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(audioDbResponse))
        });

        // Act
        var result = await _service.GetArtistMetadataAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    #endregion
}
