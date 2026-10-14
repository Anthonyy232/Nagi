using System.Net;
using System.Text.Json;
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

public class FanartTvServiceTests : IDisposable
{
    private const string ValidMbid = "b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d";

    private readonly TestHttpMessageHandler _httpHandler;
    private readonly FanartTvService _service;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<FanartTvService> _logger;

    private readonly ProviderPipelineProvider _pipelines;

    public FanartTvServiceTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://webservice.fanart.tv") };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _apiKeyService = Substitute.For<IApiKeyService>();
        _apiKeyService.GetApiKeyAsync("fanarttv", Arg.Any<CancellationToken>()).Returns("test-api-key");

        _logger = Substitute.For<ILogger<FanartTvService>>();

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.FanartTv);
        _service = new FanartTvService(httpClientFactory, _pipelines, _apiKeyService, _logger);
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpHandler.Dispose();
    }

    [Fact]
    public async Task GetArtistImagesAsync_WithValidMbid_ReturnsAllImageTypes()
    {
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
        var result = await _service.GetArtistImagesAsync(ValidMbid);
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
        var fanartResponse = new
        {
            artistthumb = new[] { new { url = "https://fanart.tv/thumb.jpg" } }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(fanartResponse))
        });
        var result = await _service.GetArtistImagesAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data!.ThumbUrl.Should().Be("https://fanart.tv/thumb.jpg");
        result.Data.BackgroundUrl.Should().BeNull();
        result.Data.LogoUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetArtistImagesAsync_PrefersHdLogoOverStandardLogo()
    {
        var fanartResponse = new
        {
            hdmusiclogo = new[] { new { url = "https://fanart.tv/logo-hd.png" } },
            musiclogo = new[] { new { url = "https://fanart.tv/logo-sd.png" } }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(fanartResponse))
        });
        var result = await _service.GetArtistImagesAsync(ValidMbid);
        result.Data!.LogoUrl.Should().Be("https://fanart.tv/logo-hd.png");
    }

    [Fact]
    public async Task GetArtistImagesAsync_When404_ReturnsSuccessNotFound()
    {
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await _service.GetArtistImagesAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetArtistImagesAsync_WithEmptyMbid_ReturnsSuccessNotFound(string mbid)
    {
        var result = await _service.GetArtistImagesAsync(mbid);
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task GetArtistImagesAsync_WhenApiKeyMissing_ReturnsTemporaryError()
    {
        _apiKeyService.GetApiKeyAsync("fanarttv", Arg.Any<CancellationToken>()).Returns((string?)null);
        var result = await _service.GetArtistImagesAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GetArtistImagesAsync_WhenHttpError_ReturnsTemporaryError(HttpStatusCode status)
    {
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(status));
        var result = await _service.GetArtistImagesAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Fact]
    public async Task GetArtistImagesAsync_WhenCancelled_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.GetArtistImagesAsync(ValidMbid, cts.Token));
    }

    [Fact]
    public async Task GetArtistImagesAsync_WithEmptyImages_ReturnsSuccessNotFound()
    {
        var fanartResponse = new
        {
            artistbackground = Array.Empty<object>(),
            hdmusiclogo = Array.Empty<object>()
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(fanartResponse))
        });
        var result = await _service.GetArtistImagesAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

}
