using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Nagi.Core.Http;

/// <summary>
///     Provides standardized retry logic for HTTP operations with exponential backoff.
/// </summary>
public static class HttpRetryHelper
{
    /// <summary>
    ///     HTTP status codes that are considered transient and should trigger a retry.
    /// </summary>
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes = new()
    {
        HttpStatusCode.InternalServerError,  // 500
        HttpStatusCode.BadGateway,           // 502
        HttpStatusCode.ServiceUnavailable,   // 503
        HttpStatusCode.GatewayTimeout        // 504
    };

    /// <summary>
    ///     Executes an HTTP operation with automatic retry for transient failures.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">
    ///     The async operation to execute. Receives the current attempt number (1-based).
    ///     Should return a tuple of (result, shouldRetry) where shouldRetry indicates if the 
    ///     operation encountered a retryable failure and should be attempted again.
    /// </param>
    /// <param name="logger">Logger for diagnostic messages.</param>
    /// <param name="operationName">Name of the operation for logging purposes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="maxRetries">Maximum number of retry attempts. Default is 3.</param>
    /// <param name="baseDelaySeconds">Base delay in seconds for exponential backoff. Default is 2.</param>
    /// <returns>The result of the operation, or default if all retries failed.</returns>
    public static async Task<T?> ExecuteWithRetryAsync<T>(
        Func<int, Task<RetryResult<T>>> operation,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default,
        int maxRetries = 3,
        int baseDelaySeconds = 2)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var result = await operation(attempt).ConfigureAwait(false);

                if (result.IsSuccess || !result.ShouldRetry || attempt >= maxRetries)
                {
                    return result.Value;
                }

                // Retry with backoff (use override if set for rate-limit scenarios)
                var delayMultiplier = result.DelayMultiplierOverride ?? baseDelaySeconds;
                logger.LogDebug("{OperationName} failed, retrying (Attempt {Attempt}/{MaxRetries})",
                    operationName, attempt, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(delayMultiplier * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientException(ex))
            {
                logger.LogWarning(ex, "Transient error in {OperationName}. Attempt {Attempt}/{MaxRetries}",
                    operationName, attempt, maxRetries);
                
                // On last attempt, don't retry - just return default
                if (attempt >= maxRetries)
                    return default;
                    
                await Task.Delay(TimeSpan.FromSeconds(baseDelaySeconds * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Non-transient error in {OperationName}. Attempt {Attempt}/{MaxRetries}",
                    operationName, attempt, maxRetries);
                return default;
            }
        }

        return default;
    }

    /// <summary>
    ///     Determines if an exception is transient and should trigger a retry.
    /// </summary>
    public static bool IsTransientException(Exception ex)
        => ex is HttpRequestException or IOException or SocketException;

    /// <summary>
    ///     Determines if an HTTP status code is transient and should trigger a retry.
    /// </summary>
    public static bool IsRetryableStatusCode(HttpStatusCode statusCode)
        => RetryableStatusCodes.Contains(statusCode);

    /// <summary>
    ///     Determines if a rate limit response should trigger a retry with longer backoff.
    /// </summary>
    public static bool IsRateLimitStatusCode(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests;
}

/// <summary>
///     Represents the result of an HTTP operation attempt, including retry guidance.
/// </summary>
/// <typeparam name="T">The type of the result value.</typeparam>
public readonly struct RetryResult<T>
{
    /// <summary>
    ///     The result value if the operation succeeded.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    ///     Indicates whether the operation completed successfully.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    ///     Indicates whether the operation should be retried if it failed.
    /// </summary>
    public bool ShouldRetry { get; }

    /// <summary>
    ///     Override delay multiplier for rate-limit scenarios. If set, the helper uses this
    ///     instead of the default baseDelaySeconds for backoff calculation.
    /// </summary>
    public int? DelayMultiplierOverride { get; }

    private RetryResult(T? value, bool isSuccess, bool shouldRetry, int? delayMultiplierOverride = null)
    {
        Value = value;
        IsSuccess = isSuccess;
        ShouldRetry = shouldRetry;
        DelayMultiplierOverride = delayMultiplierOverride;
    }

    /// <summary>
    ///     Creates a successful result that should not be retried.
    /// </summary>
    public static RetryResult<T> Success(T value) => new(value, true, false);

    /// <summary>
    ///     Creates a successful result with no value (e.g., resource not found).
    /// </summary>
    public static RetryResult<T> SuccessEmpty() => new(default, true, false);

    /// <summary>
    ///     Creates a failed result that should be retried with standard backoff.
    /// </summary>
    public static RetryResult<T> TransientFailure() => new(default, false, true);

    /// <summary>
    ///     Creates a failed result that should be retried with a custom delay multiplier.
    ///     Use this for rate-limit scenarios that require longer delays.
    /// </summary>
    /// <param name="delayMultiplierSeconds">Base delay multiplier in seconds (delay = multiplier * attempt).</param>
    public static RetryResult<T> RateLimitFailure(int delayMultiplierSeconds) => new(default, false, true, delayMultiplierSeconds);

    /// <summary>
    ///     Creates a failed result that should not be retried.
    /// </summary>
    public static RetryResult<T> PermanentFailure() => new(default, false, false);

    /// <summary>
    ///     Creates a result based on HTTP response status code.
    /// </summary>
    public static RetryResult<T> FromHttpStatus(HttpStatusCode statusCode)
    {
        return HttpRetryHelper.IsRetryableStatusCode(statusCode)
            ? TransientFailure()
            : PermanentFailure();
    }
}
