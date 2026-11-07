using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Library;
using Nagi.Core.Lyrics;
using Nagi.Core.Models;
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
    public async Task GetLyricsAsync_ReplacesTheDefaultUserAgentWithABrowserOne()
    {
        using var handler = new TestHttpMessageHandler();
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nagi/1.0");
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        handler.RespondSequence((HttpStatusCode.OK, "{}"));

        await new NetEaseLyricsService(factory, _pipelines, _logger).GetLyricsAsync(Track("Track", null));

        handler.Requests[0].Headers.UserAgent.ToString().Should().StartWith("Mozilla/5.0").And.NotContain("Nagi");
    }

    [Fact]
    public async Task GetLyricsAsync_PrefersExactTitleThenMatchingArtist()
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

        var lyrics = await _service.GetLyricsAsync(Track(" Track ", " The Artist "));

        lyrics.Data.Should().Be("[00:01.00]Match");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" Unknown Artist ")]
    public async Task GetLyricsAsync_WithMissingArtist_SearchesByTitle(string? artist)
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

        var result = await _service.GetLyricsAsync(Track("Track Name", artist));

        result.Data.Should().Contain("[00:01.00]Found It");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetLyricsAsync_WithBlankTrackName_ReturnsNull(string track)
    {
        var result = await _service.GetLyricsAsync(Track(track, "Artist"));

        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task GetLyricsAsync_WhenSearchReturnsNoResults_ReturnsNull()
    {
        var searchResponse = new { result = new { songs = Array.Empty<object>() } };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(searchResponse))
        });

        var result = await _service.GetLyricsAsync(Track("NonExistent Track", "Artist"));

        result.Data.Should().BeNull();
    }

    [Theory]
    [InlineData("Plain text lyrics without timestamps", false)]
    [InlineData("[00:01 broken timestamp", false)]
    [InlineData("[0:01.123]Short minutes", true)]
    [InlineData("[100:01.00]Long minutes", true)]
    public async Task GetLyricsAsync_ValidatesLyricsWithPlaybackParser(string lyrics, bool valid)
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

        var result = await _service.GetLyricsAsync(Track("Track", "Artist"));

        result.Data.Should().Be(valid ? lyrics : null);
    }

    [Fact]
    public async Task GetLyricsAsync_WhenRejected_RemainsRetryable()
    {
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));

        (await _service.GetLyricsAsync(Track("Track1", "Artist"))).IsConclusive.Should().BeFalse();
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"result\": {\"songs\": [{\"id\": 123}]}}")
        });
        var result2 = await _service.GetLyricsAsync(Track("Track2", "Artist"));
        result2.Data.Should().BeNull();
    }

    [Fact]
    public async Task GetLyricsAsync_WhenLyricsFetchReturns403_RemainsRetryable()
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
        (await _service.GetLyricsAsync(Track("Track1", "Artist"))).IsConclusive.Should().BeFalse();
    }

    [Fact]
    public async Task GetLyricsAsync_WhenAlreadyCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.GetLyricsAsync(Track("Track", "Artist"), cts.Token));
    }

    [Fact]
    public async Task GetLyricsAsync_WhenCancelledDuringLyricsFetch_ThrowsOperationCanceledException()
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
            () => _service.GetLyricsAsync(Track("Track", "Artist"), cts.Token));
    }

    private static Song Track(string title, string? artist) => new() { Title = title, PrimaryArtistName = artist ?? string.Empty };
}
