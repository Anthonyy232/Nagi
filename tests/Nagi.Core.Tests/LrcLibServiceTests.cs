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
///     Comprehensive tests for the LrcLibService covering successful fetches,
///     fallback behavior, cancellation, and rate limiting.
/// </summary>
public class LrcLibServiceTests : IDisposable
{
    private const string StrictMatchUrl = "https://lrclib.net/api/get";
    private const string SearchUrl = "https://lrclib.net/api/search";

    private readonly TestHttpMessageHandler _httpHandler;
    private readonly LrcLibService _service;
    private readonly ILogger<LrcLibService> _logger;

    public LrcLibServiceTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://lrclib.net") };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        _logger = Substitute.For<ILogger<LrcLibService>>();

        _service = new LrcLibService(httpClientFactory, _logger);
    }

    public void Dispose()
    {
        _httpHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Strict Match Tests

    [Fact]
    public async Task GetLyricsAsync_WithValidMetadata_ReturnsSyncedLyrics()
    {
        // Arrange
        var lrcResponse = new { syncedLyrics = "[00:01.00]Hello World" };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(lrcResponse))
        });

        // Act
        var result = await _service.GetLyricsAsync("Test Track", "Test Artist", "Test Album", TimeSpan.FromMinutes(3));

        // Assert
        result.Should().Be("[00:01.00]Hello World");
    }

    [Fact]
    public async Task GetLyricsAsync_WithEmptyTrackName_ReturnsNull()
    {
        // Act
        var result = await _service.GetLyricsAsync("", "Artist", "Album", TimeSpan.FromMinutes(3));

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLyricsAsync_WithWhitespaceTrackName_ReturnsNull()
    {
        // Act
        var result = await _service.GetLyricsAsync("   ", "Artist", "Album", TimeSpan.FromMinutes(3));

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Search Fallback Tests

    [Fact]
    public async Task GetLyricsAsync_WhenStrictMatchReturns404_FallsBackToSearch()
    {
        // Arrange
        var callCount = 0;
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            callCount++;
            if (request.RequestUri!.AbsolutePath.Contains("/api/get"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // Search endpoint returns a matching result
            var searchResults = new[]
            {
                new { syncedLyrics = "[00:01.00]Fallback Lyrics", duration = 180.0, albumName = "Test Album" }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(searchResults))
            });
        };

        // Act
        var result = await _service.GetLyricsAsync("Test Track", "Test Artist", "Test Album", TimeSpan.FromMinutes(3));

        // Assert
        result.Should().Be("[00:01.00]Fallback Lyrics");
        callCount.Should().Be(2); // Strict match + search
    }

    [Fact]
    public async Task GetLyricsAsync_WhenSearchReturnsNoDurationMatch_ReturnsNull()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/get"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // Search returns result with duration way off (more than 30 seconds)
            var searchResults = new[]
            {
                new { syncedLyrics = "[00:01.00]Wrong Song", duration = 500.0, albumName = "Test Album" }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(searchResults))
            });
        };

        // Act
        var result = await _service.GetLyricsAsync("Test Track", "Test Artist", "Test Album", TimeSpan.FromMinutes(3));

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLyricsAsync_SearchPrefersDurationWithin30Seconds()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/get"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // Search returns multiple results - one within tolerance, one outside
            var searchResults = new[]
            {
                new { syncedLyrics = "[00:01.00]Correct Match", duration = 185.0, albumName = "Test Album" },
                new { syncedLyrics = "[00:01.00]Wrong Match", duration = 300.0, albumName = "Test Album" }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(searchResults))
            });
        };

        // Act
        var result = await _service.GetLyricsAsync("Test Track", "Test Artist", "Test Album", TimeSpan.FromMinutes(3));

        // Assert
        result.Should().Be("[00:01.00]Correct Match");
    }

    #endregion

    #region Rate Limiting Tests

    [Fact]
    public async Task GetLyricsAsync_WhenRateLimited_DisablesServiceForSession()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        // Act - First call triggers rate limit
        var result1 = await _service.GetLyricsAsync("Track1", "Artist", "Album", TimeSpan.FromMinutes(3));

        // Reset handler to return success (but service should be disabled)
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"syncedLyrics\": \"[00:01.00]Lyrics\"}")
        });

        // Act - Second call should be short-circuited
        var result2 = await _service.GetLyricsAsync("Track2", "Artist", "Album", TimeSpan.FromMinutes(3));

        // Assert
        result1.Should().BeNull();
        result2.Should().BeNull(); // Should still be null because service is disabled
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task GetLyricsAsync_WhenCancelled_ReturnsNull()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        _httpHandler.SendAsyncFunc = async (_, ct) =>
        {
            await Task.Delay(100, ct); // Simulate network delay
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        // Act
        cts.Cancel();
        var result = await _service.GetLyricsAsync("Track", "Artist", "Album", TimeSpan.FromMinutes(3), cts.Token);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLyricsAsync_WhenCancelledDuringSearch_ReturnsNull()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var callCount = 0;

        _httpHandler.SendAsyncFunc = async (request, ct) =>
        {
            callCount++;
            if (request.RequestUri!.AbsolutePath.Contains("/api/get"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // Cancel during search
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        // Act
        var result = await _service.GetLyricsAsync("Track", "Artist", "Album", TimeSpan.FromMinutes(3), cts.Token);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Missing Metadata Handling

    [Fact]
    public async Task GetLyricsAsync_WithMissingArtist_SkipsStrictMatchAndSearches()
    {
        // Arrange
        var callCount = 0;
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            callCount++;
            // Should skip strict match and go directly to search
            var searchResults = new[]
            {
                new { syncedLyrics = "[00:01.00]Search Result", duration = 180.0 }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(searchResults))
            });
        };

        // Act
        var result = await _service.GetLyricsAsync("Track", null, "Album", TimeSpan.FromMinutes(3));

        // Assert
        result.Should().Be("[00:01.00]Search Result");
        callCount.Should().Be(1); // Only search, no strict match
    }

    [Fact]
    public async Task GetLyricsAsync_WithUnknownArtistPlaceholder_SkipsStrictMatch()
    {
        // Arrange
        var callCount = 0;
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            callCount++;
            var searchResults = new[]
            {
                new { syncedLyrics = "[00:01.00]Found It", duration = 180.0 }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(searchResults))
            });
        };

        // Act
        var result = await _service.GetLyricsAsync("Track", "Unknown Artist", "Album", TimeSpan.FromMinutes(3));

        // Assert
        result.Should().Be("[00:01.00]Found It");
        callCount.Should().Be(1); // Only search
    }

    #endregion
}
