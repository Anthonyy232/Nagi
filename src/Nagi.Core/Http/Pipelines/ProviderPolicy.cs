namespace Nagi.Core.Http.Pipelines;

/// <summary>
///     Rate limiting and retry settings shared by calls to one provider.
/// </summary>
public sealed record ChannelPolicy
{
    /// <summary>Permits granted per <see cref="Window"/>, which together define the sustained rate.</summary>
    public required int PermitsPerWindow { get; init; }

    /// <summary>
    ///     Replenishment window. Use a larger window for rates under one request a second, for example 1 permit per
    ///     2 seconds for 30 a minute.
    /// </summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(1);

    public required int MaxConcurrent { get; init; }

    /// <summary>Retries for transient failures (5xx, 429, network errors).</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Base of the exponential backoff between retries, before jitter.</summary>
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Cap for any single retry delay. Longer <c>Retry-After</c> headers are clamped so a misbehaving service
    ///     can't block calls indefinitely.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
///     One rate-limit/retry pipeline plus a circuit breaker, shared by every call that uses the same id.
/// </summary>
public sealed record ProviderPolicy
{
    public required string ProviderId { get; init; }

    public required ChannelPolicy Channel { get; init; }

    public CircuitBreakerSettings CircuitBreaker { get; init; } = new();
}

/// <summary>
///     Trips after a small number of failures and waits before allowing a half-open trial.
/// </summary>
public sealed record CircuitBreakerSettings
{
    /// <summary>
    ///     Calls needed within the sampling window before the failure ratio is evaluated. Low, so consecutive
    ///     failures trip the breaker quickly.
    /// </summary>
    public int MinimumThroughput { get; init; } = 3;

    public TimeSpan SamplingDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Failure ratio that trips the breaker, from 0 to 1.</summary>
    public double FailureRatio { get; init; } = 1.0;

    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromMinutes(10);
}
