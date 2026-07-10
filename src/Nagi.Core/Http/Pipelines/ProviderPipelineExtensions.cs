using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace Nagi.Core.Http.Pipelines;

/// <summary>
///     Convenience helpers over <see cref="IProviderPipelineProvider"/> that collapse the
///     repeated try/catch skeleton (circuit-open, transport failure, unexpected exception)
///     into one call. The supplied <paramref name="processResponse"/> delegate receives the
///     <see cref="HttpResponseMessage"/> already wrapped in a using scope, so callers do not
///     need to dispose it themselves.
/// </summary>
public static class ProviderPipelineExtensions
{
    /// <summary>
    ///     Executes <paramref name="httpCall"/> through the provider's pipeline, feeds the
    ///     response into <paramref name="processResponse"/>, and returns the result. Returns
    ///     <paramref name="fallback"/> on a broken circuit, transport failure, provider timeout,
    ///     or any other non-cancellation exception. Caller-requested cancellation is not swallowed.
    /// </summary>
    public static async ValueTask<T> ExecuteWithFallbackAsync<T>(
        this IProviderPipelineProvider pipelines,
        string providerId,
        Func<CancellationToken, ValueTask<HttpResponseMessage>> httpCall,
        Func<HttpResponseMessage, CancellationToken, ValueTask<T>> processResponse,
        T fallback,
        ILogger logger,
        string operationDescription,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await pipelines
                .ExecuteAsync(providerId, httpCall, cancellationToken).ConfigureAwait(false);
            return await processResponse(response, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokenCircuitException)
        {
            logger.LogDebug("{ProviderId} circuit is open; skipping {Operation}.",
                providerId, operationDescription);
            return fallback;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as TaskCanceledException/OperationCanceledException.
            // Only cancellation requested by our caller should escape this fallback boundary.
            logger.LogWarning(ex, "{ProviderId} timed out during {Operation}.",
                providerId, operationDescription);
            return fallback;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "{ProviderId} transport failure during {Operation}.",
                providerId, operationDescription);
            return fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{ProviderId} unexpected error during {Operation}.",
                providerId, operationDescription);
            return fallback;
        }
    }
}
