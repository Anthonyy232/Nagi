using System.Net;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Services.Data;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class MusicBrainzServiceTests : IDisposable
{
    private readonly TestHttpMessageHandler _httpHandler;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MusicBrainzService _service;
    private readonly ProviderPipelineProvider _pipelines;

    public MusicBrainzServiceTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://musicbrainz.org") };
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.MusicBrainz);
        _service = new MusicBrainzService(_httpClientFactory, _pipelines, NullLogger<MusicBrainzService>.Instance);
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpHandler.Dispose();
    }

    [Fact]
    public async Task SearchArtistAsync_WithValidArtist_ReturnsMusicBrainzId()
    {
        var mbResponse = new
        {
            artists = new[]
            {
                new { id = "b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d", name = "The Beatles", score = 100 }
            }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(mbResponse))
        });
        var result = await _service.SearchArtistAsync("The Beatles");
        result.Data.Should().Be("b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d");
    }

    [Fact]
    public async Task SearchArtistAsync_WithLowScoreResult_ReturnsNoMatch()
    {
        var mbResponse = new
        {
            artists = new[]
            {
                new { id = "some-id", name = "Similar Artist", score = 50 }
            }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(mbResponse))
        });
        var result = await _service.SearchArtistAsync("The Beatles");
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task SearchArtistAsync_WithNoResults_ReturnsNoMatch()
    {
        var mbResponse = new { artists = Array.Empty<object>() };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(mbResponse))
        });
        var result = await _service.SearchArtistAsync("NonExistent Artist XYZ123");
        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Theory]
    [InlineData("The Rolling Stones", "artist:\"The Rolling Stones\"")]
    [InlineData("The \"Artist\"", "artist:\"The \\\"Artist\\\"\"")]
    [InlineData("AC\\DC", "artist:\"AC\\\\DC\"")]
    [InlineData("Björk & Friends", "artist:\"Björk & Friends\"")]
    public async Task SearchArtistAsync_QuotesNameInQuery(string artistName, string expectedQuery)
    {
        string? capturedUrl = null;
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            capturedUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"artists\": []}")
            });
        };

        await _service.SearchArtistAsync(artistName);

        HttpUtility.ParseQueryString(new Uri(capturedUrl!).Query)["query"].Should().Be(expectedQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task SearchArtistAsync_HttpFailure_RemainsRetryable(HttpStatusCode status)
    {
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(status));

        var result = await _service.SearchArtistAsync("Any Artist");

        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
        result.Data.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchArtistAsync_EmptyName_DoesNotSendRequest(string artistName)
    {
        var calls = 0;
        _httpHandler.SendAsyncFunc = (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("No request expected");
        };

        var result = await _service.SearchArtistAsync(artistName);

        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
        calls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchArtistAsync_TransportFailure_RemainsRetryable(bool timeout)
    {
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromException<HttpResponseMessage>(
            timeout ? new TaskCanceledException("Timed out") : new HttpRequestException("Offline"));

        var result = await _service.SearchArtistAsync("Any Artist");

        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Fact]
    public async Task SearchArtistAsync_WhenRequestIsCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _httpHandler.SendAsyncFunc = async (_, ct) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var request = _service.SearchArtistAsync("Artist", cts.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task SearchReleaseGroupAsync_PrefersOriginalAlbumOverFanMadeRemix()
    {
        const string response = """
                                {
                                  "release-groups": [
                                    { "id": "fanmade", "score": 100, "title": "Hybrid Theory", "primary-type": "Album",
                                      "secondary-types": ["Remix"], "first-release-date": "2023-07-21" },
                                    { "id": "official", "score": 100, "title": "Hybrid Theory", "primary-type": "Album",
                                      "first-release-date": "2000-05-07" }
                                  ]
                                }
                                """;
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response)
        });

        var result = await _service.SearchReleaseGroupAsync("Hybrid Theory", "Linkin Park");

        result.Data.Should().Be("official");
    }

    [Fact]
    public async Task SearchReleaseGroupAsync_QuotesAlbumAndArtistInQuery()
    {
        string? capturedUrl = null;
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            capturedUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"release-groups\": []}")
            });
        };

        await _service.SearchReleaseGroupAsync("Hybrid \"Theory\"", "Linkin Park");

        HttpUtility.ParseQueryString(new Uri(capturedUrl!).Query)["query"].Should()
            .Be("releasegroup:\"Hybrid \\\"Theory\\\"\" AND artist:\"Linkin Park\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchReleaseGroupAsync_EmptyAlbum_DoesNotSendRequest(string albumTitle)
    {
        var calls = 0;
        _httpHandler.SendAsyncFunc = (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("No request expected");
        };

        var result = await _service.SearchReleaseGroupAsync(albumTitle, "Linkin Park");

        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
        calls.Should().Be(0);
    }
}
