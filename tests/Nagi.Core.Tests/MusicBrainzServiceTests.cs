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
///     Comprehensive tests for the MusicBrainzService covering artist lookup,
///     rate limiting, Lucene query syntax, and error handling.
/// </summary>
public class MusicBrainzServiceTests : IDisposable
{
    private readonly TestHttpMessageHandler _httpHandler;
    private readonly MusicBrainzService _service;
    private readonly ILogger<MusicBrainzService> _logger;

    public MusicBrainzServiceTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("https://musicbrainz.org") };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        _logger = Substitute.For<ILogger<MusicBrainzService>>();

        _service = new MusicBrainzService(httpClientFactory, _logger);
    }

    public void Dispose()
    {
        _httpHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Successful Lookup Tests

    [Fact]
    public async Task SearchArtistAsync_WithValidArtist_ReturnsMusicBrainzId()
    {
        // Arrange
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

        // Act
        var result = await _service.SearchArtistAsync("The Beatles");

        // Assert
        result.Should().Be("b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d");
    }

    [Fact]
    public async Task SearchArtistAsync_WithLowScoreResult_ReturnsNull()
    {
        // Arrange
        var mbResponse = new
        {
            artists = new[]
            {
                new { id = "some-id", name = "Similar Artist", score = 50 } // Below 80 threshold
            }
        };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(mbResponse))
        });

        // Act
        var result = await _service.SearchArtistAsync("The Beatles");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchArtistAsync_WithNoResults_ReturnsNull()
    {
        // Arrange
        var mbResponse = new { artists = Array.Empty<object>() };
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(mbResponse))
        });

        // Act
        var result = await _service.SearchArtistAsync("NonExistent Artist XYZ123");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Lucene Query Syntax Tests

    [Fact]
    public async Task SearchArtistAsync_WithMultiWordArtist_QuotesNameInQuery()
    {
        // Arrange
        string? capturedUrl = null;
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            capturedUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"artists\": []}")
            });
        };

        // Act
        await _service.SearchArtistAsync("The Rolling Stones");

        // Assert - URL should contain quoted artist name for Lucene (may be decoded in Uri.ToString())
        capturedUrl.Should().Contain("\"The Rolling Stones\"");
    }

    [Fact]
    public async Task SearchArtistAsync_WithSingleWordArtist_StillQuotesName()
    {
        // Arrange
        string? capturedUrl = null;
        _httpHandler.SendAsyncFunc = (request, _) =>
        {
            capturedUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"artists\": []}")
            });
        };

        // Act
        await _service.SearchArtistAsync("Madonna");

        // Assert - Even single word should be quoted for consistency (may be decoded in Uri.ToString())
        capturedUrl.Should().Contain("\"Madonna\"");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task SearchArtistAsync_When503ServiceUnavailable_ReturnsNull()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        // Act
        var result = await _service.SearchArtistAsync("Any Artist");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchArtistAsync_WhenOtherHttpError_ReturnsNull()
    {
        // Arrange
        _httpHandler.SendAsyncFunc = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        // Act
        var result = await _service.SearchArtistAsync("Any Artist");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchArtistAsync_WithEmptyArtistName_ReturnsNull()
    {
        // Act
        var result = await _service.SearchArtistAsync("");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchArtistAsync_WithWhitespaceArtistName_ReturnsNull()
    {
        // Act
        var result = await _service.SearchArtistAsync("   ");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task SearchArtistAsync_WhenCancelled_ThrowsTaskCancelledException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"artists\": [{\"id\": \"test-id\", \"score\": 100}]}")
        });

        // Act & Assert - Cancelled token should throw
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.SearchArtistAsync("Artist", cts.Token));
    }

    [Fact]
    public async Task SearchArtistAsync_WhenCancelledDuringDelay_ThrowsTaskCancelledException()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        _httpHandler.SendAsyncFunc = async (_, ct) =>
        {
            // Simulate the rate limit delay being cancelled
            cts.CancelAfter(10);
            await Task.Delay(1000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        // Act & Assert - Cancelled token during delay should throw
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.SearchArtistAsync("Artist", cts.Token));
    }

    #endregion

    #region Rate Limiting Tests

    [Fact]
    public async Task SearchArtistAsync_MultipleRapidCalls_RespectRateLimit()
    {
        // Arrange
        var callTimestamps = new List<DateTime>();
        _httpHandler.SendAsyncFunc = (_, _) =>
        {
            callTimestamps.Add(DateTime.UtcNow);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"artists\": [{\"id\": \"test-id\", \"name\": \"Artist\", \"score\": 100}]}")
            });
        };

        // Act - Make two rapid calls
        var task1 = _service.SearchArtistAsync("Artist 1");
        var task2 = _service.SearchArtistAsync("Artist 2");
        await Task.WhenAll(task1, task2);

        // Assert - Second call should be delayed by at least ~1 second
        if (callTimestamps.Count == 2)
        {
            var timeDiff = callTimestamps[1] - callTimestamps[0];
            timeDiff.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(900); // Allow some tolerance
        }
    }

    #endregion
}
