namespace Nagi.Core.Http.Pipelines;

/// <summary>
///     Executes HTTP calls for a provider through its configured resilience pipeline
///     (rate limit + retry + circuit breaker). Provider services depend on this instead
///     of rolling their own static semaphores and retry helpers.
/// </summary>
public interface IProviderPipelineProvider
{
    /// <summary>
    ///     Executes an HTTP call through the provider's pipeline. The action must return
    ///     the <see cref="HttpResponseMessage"/> directly so the pipeline can inspect
    ///     status codes for retry / breaker decisions. Deserialization happens at the
    ///     call site after this returns.
    /// </summary>
    /// <exception cref="Polly.CircuitBreaker.BrokenCircuitException">
    ///     Thrown when the provider's circuit breaker is open. Callers should treat this
    ///     as "skip this provider for now" rather than retrying.
    /// </exception>
    /// <exception cref="Polly.RateLimiting.RateLimiterRejectedException">
    ///     Thrown only if the rate limiter is configured to reject (it queues by default).
    /// </exception>
    ValueTask<HttpResponseMessage> ExecuteAsync(
        string providerId,
        Func<CancellationToken, ValueTask<HttpResponseMessage>> httpCall,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns true if the provider's circuit breaker is currently open (or isolated).
    ///     Callers can use this for an early exit without paying the rate-limit wait.
    /// </summary>
    bool IsCircuitOpen(string providerId);

    /// <summary>
    ///     Returns the policy registered for the given provider, or null if the provider
    ///     wasn't registered. Useful for diagnostics.
    /// </summary>
    ProviderPolicy? GetPolicy(string providerId);
}
