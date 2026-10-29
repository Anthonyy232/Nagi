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

public class TheAudioDbServiceTests : IDisposable
{
    private const string ValidMbid = "cc197bad-dc9c-440d-a5b5-d52ba2e14234";

    private readonly TestHttpMessageHandler _httpHandler;
    private readonly TheAudioDbService _service;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<TheAudioDbService> _logger;

    private readonly ProviderPipelineProvider _pipelines;

    public TheAudioDbServiceTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://www.theaudiodb.com") };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _apiKeyService = Substitute.For<IApiKeyService>();
        _apiKeyService.GetApiKeyAsync("theaudiodb", Arg.Any<CancellationToken>()).Returns("123");

        _logger = Substitute.For<ILogger<TheAudioDbService>>();

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.TheAudioDb);

        _service = new TheAudioDbService(httpClientFactory, _pipelines, _apiKeyService, _logger);
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpHandler.Dispose();
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WithValidMbid_ReturnsMetadata()
    {
        var audioDbResponse = new
        {
            artists = new[]
            {
                new
                {
                    strBiographyEN = "A famous rock band from the UK.",
                    strArtistThumb = "https://theaudiodb.com/thumb.jpg",
                    strArtistFanart = "https://theaudiodb.com/fanart.jpg"
                }
            }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(audioDbResponse))
        });
        var result = await _service.GetArtistMetadataAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data.Should().NotBeNull();
        result.Data!.Biography.Should().Be("A famous rock band from the UK.");
        result.Data.ThumbUrl.Should().Be("https://theaudiodb.com/thumb.jpg");
        result.Data.FanartUrl.Should().Be("https://theaudiodb.com/fanart.jpg");
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WithPartialData_ReturnsPartialResult()
    {
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
        var result = await _service.GetArtistMetadataAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data!.Biography.Should().Be("Bio only artist.");
        result.Data.ThumbUrl.Should().BeNull();
        result.Data.FanartUrl.Should().BeNull();
    }

    [Theory]
    [InlineData(" https://theaudiodb.com/second.jpg ", "https://theaudiodb.com/second.jpg")]
    [InlineData(null, "https://theaudiodb.com/third.jpg")]
    public async Task GetArtistMetadataAsync_SkipsBlankFanartAndTrimsSelectedUrl(string? second, string expected)
    {
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                artists = new[]
                {
                    new
                    {
                        strArtistFanart = "   ",
                        strArtistFanart2 = second,
                        strArtistFanart3 = " https://theaudiodb.com/third.jpg "
                    }
                }
            }))
        });

        var result = await _service.GetArtistMetadataAsync(ValidMbid);

        result.Data!.FanartUrl.Should().Be(expected);
    }

    [Theory]
    [InlineData("de", "German Bio", "German Bio")]
    [InlineData("DE-de", " German Bio ", "German Bio")]
    [InlineData("de-DE", "   ", "English Bio")]
    [InlineData("de", null, "English Bio")]
    [InlineData("fr", "German Bio", "English Bio")]
    [InlineData(null, "German Bio", "English Bio")]
    [InlineData("ja", null, "Japanese Bio")]
    [InlineData("fil", "German Bio", "English Bio")]
    public async Task GetArtistMetadataAsync_SelectsLocalizedBiographyOrEnglish(string? language, string? localizedBio, string expected)
    {
        var audioDbResponse = new
        {
            artists = new[]
            {
                new
                {
                    strBiographyEN = "English Bio",
                    strBiographyDe = localizedBio,
                    strBiographyJP = "Japanese Bio",
                    // Cutting "fil" to two letters would wrongly pick this one.
                    strBiographyFI = "Finnish Bio"
                }
            }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(audioDbResponse))
        });
        var result = await _service.GetArtistMetadataAsync(ValidMbid, language);
        result.Status.Should().Be(ServiceResultStatus.Success);
        result.Data!.Biography.Should().Be(expected);
    }

    [Fact]
    public async Task GetArtistMetadataAsync_When404_ReturnsSuccessNotFound()
    {
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await _service.GetArtistMetadataAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetArtistMetadataAsync_WithEmptyMbid_ReturnsSuccessNotFound(string mbid)
    {
        var result = await _service.GetArtistMetadataAsync(mbid);
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WhenApiKeyMissing_ReturnsTemporaryError()
    {
        _apiKeyService.GetApiKeyAsync("theaudiodb", Arg.Any<CancellationToken>()).Returns((string?)null);
        var result = await _service.GetArtistMetadataAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WhenHttpError_ReturnsTemporaryError()
    {
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var result = await _service.GetArtistMetadataAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WhenCancelled_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.GetArtistMetadataAsync(ValidMbid, null, cts.Token));
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WithEmptyArtistData_ReturnsSuccessNotFound()
    {
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
        var result = await _service.GetArtistMetadataAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task GetArtistMetadataAsync_WithNullArtistsArray_ReturnsSuccessNotFound()
    {
        var audioDbResponse = new { artists = (object[]?)null };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(audioDbResponse))
        });
        var result = await _service.GetArtistMetadataAsync(ValidMbid);
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

}
