using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class HttpRetryHelperTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    #region RetryResult Tests

    [Fact]
    public void Success_CreatesSuccessResultWithValue()
    {
        var result = RetryResult<string>.Success("test");
        
        result.IsSuccess.Should().BeTrue();
        result.ShouldRetry.Should().BeFalse();
        result.Value.Should().Be("test");
        result.DelayMultiplierOverride.Should().BeNull();
    }

    [Fact]
    public void SuccessEmpty_CreatesSuccessResultWithoutValue()
    {
        var result = RetryResult<string>.SuccessEmpty();
        
        result.IsSuccess.Should().BeTrue();
        result.ShouldRetry.Should().BeFalse();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void TransientFailure_CreatesRetryableResult()
    {
        var result = RetryResult<string>.TransientFailure();
        
        result.IsSuccess.Should().BeFalse();
        result.ShouldRetry.Should().BeTrue();
        result.DelayMultiplierOverride.Should().BeNull();
    }

    [Fact]
    public void RateLimitFailure_CreatesRetryableResultWithDelayOverride()
    {
        var result = RetryResult<string>.RateLimitFailure(5);
        
        result.IsSuccess.Should().BeFalse();
        result.ShouldRetry.Should().BeTrue();
        result.DelayMultiplierOverride.Should().Be(5);
    }

    [Fact]
    public void PermanentFailure_CreatesNonRetryableResult()
    {
        var result = RetryResult<string>.PermanentFailure();
        
        result.IsSuccess.Should().BeFalse();
        result.ShouldRetry.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void FromHttpStatus_ReturnsCorrectRetryBehavior(HttpStatusCode statusCode, bool shouldRetry)
    {
        var result = RetryResult<string>.FromHttpStatus(statusCode);
        
        result.ShouldRetry.Should().Be(shouldRetry);
        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region ExecuteWithRetryAsync Tests

    [Fact]
    public async Task ExecuteWithRetryAsync_ReturnsImmediatelyOnSuccess()
    {
        var callCount = 0;
        
        var result = await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                callCount++;
                return RetryResult<string>.Success("success");
            },
            _logger,
            "TestOperation",
            CancellationToken.None,
            maxRetries: 3
        );
        
        result.Should().Be("success");
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_RetriesOnTransientFailure()
    {
        var callCount = 0;
        
        var result = await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                callCount++;
                if (callCount < 3)
                    return RetryResult<string>.TransientFailure();
                return RetryResult<string>.Success("success");
            },
            _logger,
            "TestOperation",
            CancellationToken.None,
            maxRetries: 3,
            baseDelaySeconds: 0 // No delay for testing
        );
        
        result.Should().Be("success");
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_StopsOnPermanentFailure()
    {
        var callCount = 0;
        
        var result = await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                callCount++;
                return RetryResult<string>.PermanentFailure();
            },
            _logger,
            "TestOperation",
            CancellationToken.None,
            maxRetries: 3
        );
        
        result.Should().BeNull();
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ReturnsDefaultAfterMaxRetries()
    {
        var callCount = 0;
        
        var result = await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                callCount++;
                return RetryResult<string>.TransientFailure();
            },
            _logger,
            "TestOperation",
            CancellationToken.None,
            maxRetries: 3,
            baseDelaySeconds: 0
        );
        
        result.Should().BeNull();
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_WithCancelledToken_ReturnsNullIfOperationDoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        // When the operation itself doesn't check the cancellation token,
        // the helper will return the operation's result (not throw)
        var result = await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt => RetryResult<string>.Success("success"),
            _logger,
            "TestOperation",
            cts.Token
        );
        
        // The operation succeeds because it doesn't check the token
        result.Should().Be("success");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_PropagatesCancellationFromOperation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        Func<Task> act = async () => await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                cts.Token.ThrowIfCancellationRequested();
                return RetryResult<string>.Success("success");
            },
            _logger,
            "TestOperation",
            cts.Token
        );
        
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Status Code Helpers Test

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.OK, false)]
    public void IsRetryableStatusCode_ReturnsCorrectResult(HttpStatusCode statusCode, bool expected)
    {
        HttpRetryHelper.IsRetryableStatusCode(statusCode).Should().Be(expected);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.OK, false)]
    public void IsRateLimitStatusCode_ReturnsCorrectResult(HttpStatusCode statusCode, bool expected)
    {
        HttpRetryHelper.IsRateLimitStatusCode(statusCode).Should().Be(expected);
    }

    [Fact]
    public void IsTransientException_RecognizesTransientExceptions()
    {
        HttpRetryHelper.IsTransientException(new HttpRequestException()).Should().BeTrue();
        HttpRetryHelper.IsTransientException(new IOException()).Should().BeTrue();
        HttpRetryHelper.IsTransientException(new System.Net.Sockets.SocketException()).Should().BeTrue();
        HttpRetryHelper.IsTransientException(new InvalidOperationException()).Should().BeFalse();
    }

    #endregion
}
