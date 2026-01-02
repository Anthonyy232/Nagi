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
///     Comprehensive tests for the FanartTvService covering image fetching,
///     error handling, rate limiting, and cancellation.
/// </summary>
public class FanartTvServiceTests : IDisposable
{
    private const string ValidMbid = "b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d";

    private readonly TestHttpMessageHandler _httpHandler;
    private readonly FanartTvService _service;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<FanartTvService> _logger;

    public FanartTvServiceTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://webservice.fanart.tv") };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _apiKeyService = Substitute.For<IApiKeyService>();
        _apiKeyService.GetApiKeyAsync("fanarttv", Arg.Any<CancellationToken>()).Returns("test-api-key");

        _logger = Substitute.For<ILogger<FanartTvService>>();

        _service = new FanartTvService(httpClientFactory, _apiKeyService, _logger);
    }

    public void Dispose()
    {
        _httpHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Successful Fetch Tests

    [Fact]
    public async Task GetArtistImagesAsync_WithValidMbid_ReturnsAllImageTypes()
    {
        // Arrange
        var fanartResponse = new
        {
            artistbackground = new[] { new { url = "https://fanart.tv/bg.jpg" } },
            hdmusiclogo = new[] { new { url = "https://fanart.tv/logo-hd.png" } },
            musicbanner = new[] { new { url = "https://fanart.tv/banner.jpg" } },
            artistthumb = new[] { new { url = "https://fanart.tv/thumb.jpg" } }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(fanartResponse))
        });

        // Act
        var result = await _service.GetArtistImagesAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data.Should().NotBeNull();
        result.Data!.BackgroundUrl.Should().Be("https://fanart.tv/bg.jpg");
        result.Data.LogoUrl.Should().Be("https://fanart.tv/logo-hd.png");
        result.Data.BannerUrl.Should().Be("https://fanart.tv/banner.jpg");
        result.Data.ThumbUrl.Should().Be("https://fanart.tv/thumb.jpg");
    }

    [Fact]
    public async Task GetArtistImagesAsync_WithOnlyThumb_ReturnsPartialResult()
    {
        // Arrange
        var fanartResponse = new
        {
            artistthumb = new[] { new { url = "https://fanart.tv/thumb.jpg" } }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(fanartResponse))
        });

        // Act
        var result = await _service.GetArtistImagesAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data!.ThumbUrl.Should().Be("https://fanart.tv/thumb.jpg");
        result.Data.BackgroundUrl.Should().BeNull();
        result.Data.LogoUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetArtistImagesAsync_PrefersHdLogoOverStandardLogo()
    {
        // Arrange
        var fanartResponse = new
        {
            hdmusiclogo = new[] { new { url = "https://fanart.tv/logo-hd.png" } },
            musiclogo = new[] { new { url = "https://fanart.tv/logo-sd.png" } }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(fanartResponse))
        });

        // Act
        var result = await _service.GetArtistImagesAsync(ValidMbid);

        // Assert
        result.Data!.LogoUrl.Should().Be("https://fanart.tv/logo-hd.png");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetArtistImagesAsync_When404_ReturnsSuccessNotFound()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        // Act
        var result = await _service.GetArtistImagesAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task GetArtistImagesAsync_WithEmptyMbid_ReturnsSuccessNotFound()
    {
        // Act
        var result = await _service.GetArtistImagesAsync("");

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task GetArtistImagesAsync_WithWhitespaceMbid_ReturnsSuccessNotFound()
    {
        // Act
        var result = await _service.GetArtistImagesAsync("   ");

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task GetArtistImagesAsync_WhenApiKeyMissing_ReturnsTemporaryError()
    {
        // Arrange
        _apiKeyService.GetApiKeyAsync("fanarttv", Arg.Any<CancellationToken>()).Returns((string?)null);

        // Act
        var result = await _service.GetArtistImagesAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Fact]
    public async Task GetArtistImagesAsync_WhenHttpError_ReturnsTemporaryError()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        // Act
        var result = await _service.GetArtistImagesAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    #endregion

    #region Rate Limiting Tests

    [Fact]
    public async Task GetArtistImagesAsync_When429RateLimited_DisablesServiceForSession()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        // Act - First call triggers rate limit
        var result1 = await _service.GetArtistImagesAsync(ValidMbid);

        // Reset handler to return success
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"artistthumb\": [{\"url\": \"https://test.jpg\"}]}")
        });

        // Act - Second call should be short-circuited
        var result2 = await _service.GetArtistImagesAsync(ValidMbid);

        // Assert
        result1.Status.Should().Be(ServiceResultStatus.PermanentError);
        result2.Status.Should().Be(ServiceResultStatus.PermanentError);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task GetArtistImagesAsync_WhenCancelled_ThrowsOperationCancelled()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - TaskCanceledException inherits from OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.GetArtistImagesAsync(ValidMbid, cts.Token));
    }

    #endregion

    #region Empty Response Handling

    [Fact]
    public async Task GetArtistImagesAsync_WithEmptyImages_ReturnsSuccessNotFound()
    {
        // Arrange - Response with all empty arrays
        var fanartResponse = new
        {
            artistbackground = Array.Empty<object>(),
            hdmusiclogo = Array.Empty<object>()
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(fanartResponse))
        });

        // Act
        var result = await _service.GetArtistImagesAsync(ValidMbid);

        // Assert
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    #endregion
}
