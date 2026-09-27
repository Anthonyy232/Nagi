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

public class LrcLibServiceTests : IAsyncDisposable
{
    private readonly TestHttpMessageHandler _httpHandler;
    private readonly LrcLibService _service;
    private readonly ILogger<LrcLibService> _logger;

    private readonly ProviderPipelineProvider _pipelines;

    public LrcLibServiceTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://lrclib.net") };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        _logger = Substitute.For<ILogger<LrcLibService>>();

        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.LrcLib);
        _service = new LrcLibService(httpClientFactory, _pipelines, _logger);
    }

    public async ValueTask DisposeAsync()
    {
        await _pipelines.DisposeAsync();
        _httpHandler.Dispose();
    }

    [Fact]
    public async Task GetLyricsAsync_WithValidMetadata_ReturnsSyncedLyrics()
    {
        var lrcResponse = new { syncedLyrics = "[00:01.00]Hello World" };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(lrcResponse))
        });

        var result = await _service.GetLyricsAsync("Test Track", "Test Artist", "Test Album", TimeSpan.FromMinutes(3));

        result.Should().Be("[00:01.00]Hello World");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetLyricsAsync_WithBlankTrackName_ReturnsNull(string track)
    {
        var result = await _service.GetLyricsAsync(track, "Artist", "Album", TimeSpan.FromMinutes(3));

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.OK)]
    public async Task GetLyricsAsync_WhenStrictMatchHasNoLyrics_FallsBackToSearch(HttpStatusCode status)
    {
        var callCount = 0;
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            callCount++;
            if (request.RequestUri!.AbsolutePath.Contains("/api/get"))
            {
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("{\"syncedLyrics\":\"   \"}")
                });
            }
            var searchResults = new[]
            {
                new { syncedLyrics = "[00:01.00]Fallback Lyrics", duration = 180.0, albumName = "Test Album" }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(searchResults))
            });
        };

        var result = await _service.GetLyricsAsync("Test Track", "Test Artist", "Test Album", TimeSpan.FromMinutes(3));

        result.Should().Be("[00:01.00]Fallback Lyrics");
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task GetLyricsAsync_WhenSearchReturnsNoDurationMatch_ReturnsNull()
    {
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/get"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            var searchResults = new[]
            {
                new { syncedLyrics = "[00:01.00]Wrong Song", duration = 500.0, albumName = "Test Album" }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(searchResults))
            });
        };

        var result = await _service.GetLyricsAsync("Test Track", "Test Artist", "Test Album", TimeSpan.FromMinutes(3));

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLyricsAsync_SearchPrefersAlbumThenClosestDurationWithNonblankLyrics()
    {
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/get"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            var searchResults = new[]
            {
                new { syncedLyrics = "[00:01.00]Other Album", duration = 180.0, albumName = "Other Album" },
                new { syncedLyrics = "[00:01.00]Farther Duration", duration = 210.0, albumName = "Test Album" },
                new { syncedLyrics = "   ", duration = 180.0, albumName = "Test Album" },
                new { syncedLyrics = "[00:01.00]Correct Match", duration = 185.0, albumName = "Test Album" },
                new { syncedLyrics = "[00:01.00]Wrong Match", duration = 300.0, albumName = "Test Album" }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(searchResults))
            });
        };

        var result = await _service.GetLyricsAsync("Test Track", "Test Artist", " Test Album ", TimeSpan.FromMinutes(3));

        result.Should().Be("[00:01.00]Correct Match");
    }

    [Fact]
    public async Task GetLyricsAsync_WhenRateLimited_PropagatesFailure()
    {
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var act = () => _service.GetLyricsAsync("Track1", "Artist", "Album", TimeSpan.FromMinutes(3));
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetLyricsAsync_WhenCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => _service.GetLyricsAsync("Track", "Artist", "Album", TimeSpan.FromMinutes(3), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetLyricsAsync_WhenCancelledDuringSearch_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();

        _httpHandler.SendAsyncFunc = (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/get"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            cts.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(ct);
        };
        var act = () => _service.GetLyricsAsync("Track", "Artist", "Album", TimeSpan.FromMinutes(3), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData(" Unknown Artist ")]
    public async Task GetLyricsAsync_WithMissingArtist_SearchesWithoutArtist(string? artist)
    {
        var callCount = 0;
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            callCount++;
            request.RequestUri!.AbsolutePath.Should().Be("/api/search");
            request.RequestUri.Query.Should().NotContain("artist_name");
            var searchResults = new[]
            {
                new { syncedLyrics = "[00:01.00]Search Result", duration = 180.0 }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(searchResults))
            });
        };

        var result = await _service.GetLyricsAsync("Track", artist, "Album", TimeSpan.FromMinutes(3));

        result.Should().Be("[00:01.00]Search Result");
        callCount.Should().Be(1);
    }

}
