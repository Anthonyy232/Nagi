using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Comprehensive tests for the NetEaseLyricsService covering lyrics search,
///     error handling, rate limiting, and LRC format validation.
/// </summary>
public class NetEaseLyricsServiceTests : IDisposable
{
    private readonly TestHttpMessageHandler _httpHandler;
    private readonly NetEaseLyricsService _service;
    private readonly ILogger<NetEaseLyricsService> _logger;

    public NetEaseLyricsServiceTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://music.163.com") };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        _logger = Substitute.For<ILogger<NetEaseLyricsService>>();

        _service = new NetEaseLyricsService(httpClientFactory, _logger);
    }

    public void Dispose()
    {
        _httpHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Successful Lookup Tests

    [Fact]
    public async Task SearchLyricsAsync_WithValidTrack_ReturnsLrcContent()
    {
        // Arrange
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

        // Act
        var result = await _service.SearchLyricsAsync("Test Track", "Test Artist");

        // Assert
        result.Should().Contain("[00:01.00]Hello World");
        callCount.Should().Be(2); // Search + Lyrics fetch
    }

    [Fact]
    public async Task SearchLyricsAsync_WithNullArtist_StillSearches()
    {
        // Arrange
        var searchResponse = new { result = new { songs = new[] { new { id = 12345L, name = "Track Name" } } } };
        var lyricsResponse = new { lrc = new { lyric = "[00:01.00]Found It" } };

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

        // Act
        var result = await _service.SearchLyricsAsync("Track Name", null);

        // Assert
        result.Should().Contain("[00:01.00]Found It");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task SearchLyricsAsync_WithEmptyTrackName_ReturnsNull()
    {
        // Act
        var result = await _service.SearchLyricsAsync("", "Artist");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchLyricsAsync_WithWhitespaceTrackName_ReturnsNull()
    {
        // Act
        var result = await _service.SearchLyricsAsync("   ", "Artist");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchLyricsAsync_WhenSearchReturnsNoResults_ReturnsNull()
    {
        // Arrange
        var searchResponse = new { result = new { songs = Array.Empty<object>() } };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(searchResponse))
        });

        // Act
        var result = await _service.SearchLyricsAsync("NonExistent Track", "Artist");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchLyricsAsync_WhenLyricsAreNotLrcFormat_ReturnsNull()
    {
        // Arrange
        var searchResponse = new { result = new { songs = new[] { new { id = 12345L, name = "Track" } } } };
        var lyricsResponse = new { lrc = new { lyric = "Plain text lyrics without timestamps" } };

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

        // Act
        var result = await _service.SearchLyricsAsync("Track", "Artist");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Rate Limiting / Blocking Tests

    [Fact]
    public async Task SearchLyricsAsync_When403Forbidden_DisablesServiceForSession()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));

        // Act - First call triggers disable
        var result1 = await _service.SearchLyricsAsync("Track1", "Artist");

        // Reset handler to return success
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"result\": {\"songs\": [{\"id\": 123}]}}")
        });

        // Act - Second call should be short-circuited
        var result2 = await _service.SearchLyricsAsync("Track2", "Artist");

        // Assert
        result1.Should().BeNull();
        result2.Should().BeNull();
    }

    [Fact]
    public async Task SearchLyricsAsync_When429TooManyRequests_DisablesServiceForSession()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        // Act
        var result1 = await _service.SearchLyricsAsync("Track1", "Artist");

        // Reset handler
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"result\": {\"songs\": [{\"id\": 123}]}}")
        });

        var result2 = await _service.SearchLyricsAsync("Track2", "Artist");

        // Assert
        result1.Should().BeNull();
        result2.Should().BeNull();
    }

    [Fact]
    public async Task SearchLyricsAsync_WhenLyricsFetchReturns403_DisablesServiceForSession()
    {
        // Arrange
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

            // Lyrics fetch returns 403
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        };

        // Act
        var result1 = await _service.SearchLyricsAsync("Track1", "Artist");

        // Second call should be blocked
        var result2 = await _service.SearchLyricsAsync("Track2", "Artist");

        // Assert
        result1.Should().BeNull();
        result2.Should().BeNull();
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task SearchLyricsAsync_WhenCancelledDuringSearch_ThrowsOperationCanceledException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - Cancelled token should throw
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.SearchLyricsAsync("Track", "Artist", cts.Token));
    }

    [Fact]
    public async Task SearchLyricsAsync_WhenCancelledDuringLyricsFetch_ThrowsOperationCanceledException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
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

            // Cancel and throw during lyrics fetch
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        // Act & Assert - Cancelled during fetch should throw
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.SearchLyricsAsync("Track", "Artist", cts.Token));
    }

    #endregion
}
