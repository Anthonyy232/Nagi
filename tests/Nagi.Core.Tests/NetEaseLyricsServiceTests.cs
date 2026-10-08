using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class NetEaseLyricsServiceTests : IAsyncDisposable
{
    private readonly TestHttpMessageHandler _httpHandler;
    private readonly NetEaseLyricsService _service;
    private readonly ILogger<NetEaseLyricsService> _logger;

    private readonly ProviderPipelineProvider _pipelines;

    public NetEaseLyricsServiceTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://music.163.com") };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        _logger = Substitute.For<ILogger<NetEaseLyricsService>>();

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.NetEase);
        _service = new NetEaseLyricsService(httpClientFactory, _pipelines, _logger);
    }

    public async ValueTask DisposeAsync()
    {
        await _pipelines.DisposeAsync();
        _httpHandler.Dispose();
    }

    [Fact]
    public async Task SearchLyricsAsync_WithValidTrack_ReturnsLrcContent()
    {
        var searchResponse = new { result = new { songs = new[] { new { id = 12345L, name = "Test Track" } } } };
        var lyricsResponse = new { lrc = new { lyric = "[00:01.00]Hello World\n[00:05.00]Goodbye" } };

        var callCount = 0;
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            callCount++;
            if (request.RequestUri!.AbsolutePath.Contains("/api/search/get"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(searchResponse))
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(lyricsResponse))
            });
        };

        var result = await _service.SearchLyricsAsync("Test Track", "Test Artist");

        result.Should().Contain("[00:01.00]Hello World");
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task SearchLyricsAsync_PrefersExactTitleThenMatchingArtist()
    {
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/search/get"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"result":{"songs":[
                            {"id":1,"name":"Track (Live)","artists":[{"name":"Artist"}]},
                            {"id":2,"name":"Track","artists":[{"name":"Other Artist"}]},
                            {"id":3,"name":" Track ","artists":[{"name":"The Artist"}]}
                        ]}}
                        """)
                });

            request.RequestUri.Query.Should().Contain("id=3&");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"lrc":{"lyric":"[00:01.00]Match"}}""")
            });
        };

        var lyrics = await _service.SearchLyricsAsync(" Track ", " The Artist ");

        lyrics.Should().Be("[00:01.00]Match");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" Unknown Artist ")]
    public async Task SearchLyricsAsync_WithMissingArtist_SearchesByTitle(string? artist)
    {
        var searchResponse = new { result = new { songs = new[] { new { id = 12345L, name = "Track Name" } } } };
        var lyricsResponse = new { lrc = new { lyric = "[00:01.00]Found It" } };

        _httpHandler.SendAsyncFunc = async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/search/get"))
            {
                var query = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync());
                query["s"].Should().Be("Track Name");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(searchResponse))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(lyricsResponse))
            };
        };

        var result = await _service.SearchLyricsAsync("Track Name", artist);

        result.Should().Contain("[00:01.00]Found It");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchLyricsAsync_WithBlankTrackName_ReturnsNull(string track)
    {
        var result = await _service.SearchLyricsAsync(track, "Artist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchLyricsAsync_WhenSearchReturnsNoResults_ReturnsNull()
    {
        var searchResponse = new { result = new { songs = Array.Empty<object>() } };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(searchResponse))
        });

        var result = await _service.SearchLyricsAsync("NonExistent Track", "Artist");

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("Plain text lyrics without timestamps", false)]
    [InlineData("[00:01 broken timestamp", false)]
    [InlineData("[0:01.123]Short minutes", true)]
    [InlineData("[100:01.00]Long minutes", true)]
    public async Task SearchLyricsAsync_ValidatesLyricsWithPlaybackParser(string lyrics, bool valid)
    {
        var searchResponse = new { result = new { songs = new[] { new { id = 12345L, name = "Track" } } } };
        var lyricsResponse = new { lrc = new { lyric = lyrics } };

        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/search/get"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(searchResponse))
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(lyricsResponse))
            });
        };

        var result = await _service.SearchLyricsAsync("Track", "Artist");

        result.Should().Be(valid ? lyrics : null);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task SearchLyricsAsync_WhenRejected_PropagatesFailureAndCanRetry(HttpStatusCode status)
    {
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(status));

        var act = () => _service.SearchLyricsAsync("Track1", "Artist");
        await act.Should().ThrowAsync<HttpRequestException>();
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"result\": {\"songs\": [{\"id\": 123}]}}")
        });
        var result2 = await _service.SearchLyricsAsync("Track2", "Artist");
        result2.Should().BeNull();
    }

    [Fact]
    public async Task SearchLyricsAsync_WhenLyricsFetchReturns403_PropagatesFailure()
    {
        var searchResponse = new { result = new { songs = new[] { new { id = 12345L, name = "Track1" } } } };

        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/search/get"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(searchResponse))
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        };
        var act = () => _service.SearchLyricsAsync("Track1", "Artist");
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task SearchLyricsAsync_WhenAlreadyCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.SearchLyricsAsync("Track", "Artist", cts.Token));
    }

    [Fact]
    public async Task SearchLyricsAsync_WhenCancelledDuringLyricsFetch_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var searchResponse = new { result = new { songs = new[] { new { id = 12345L, name = "Track" } } } };

        _httpHandler.SendAsyncFunc = (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/search/get"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(searchResponse))
                });
            }
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.SearchLyricsAsync("Track", "Artist", cts.Token));
    }
}
