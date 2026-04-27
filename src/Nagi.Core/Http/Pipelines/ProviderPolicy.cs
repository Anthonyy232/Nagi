namespace Nagi.Core.Http.Pipelines;

/// <summary>
///     Per-channel resilience policy. Controls rate limiting and retry behavior for one
///     channel of one provider. Sensible defaults are aimed at "polite to the upstream API"
///     rather than "fastest possible throughput".
/// </summary>
public sealed record ChannelPolicy
{
    /// <summary>Permits granted per <see cref="Window"/>. Combined with <see cref="Window"/> this defines the sustained rate.</summary>
    public required int PermitsPerWindow { get; init; }

    /// <summary>
    ///     Replenishment window. Defaults to 1 second; set to a larger window for sub-1-RPS rates
    ///     (e.g., TheAudioDB's 30/min is expressed as 1 permit per 2-second window).
    /// </summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum concurrent in-flight requests for this channel.</summary>
    public required int MaxConcurrent { get; init; }

    /// <summary>Maximum retry attempts for transient failures (5xx, 429, network errors).</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Base delay used for exponential backoff between retries (before jitter).</summary>
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Hard cap for any single retry delay. Provider <c>Retry-After</c> headers longer
    ///     than this will be clamped to avoid blocking forever on a misbehaving service.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
///     Provider-scoped resilience policy. One rate-limit/retry pipeline plus a circuit
///     breaker per provider id.
/// </summary>
public sealed record ProviderPolicy
{
    /// <summary>Stable provider id; matches <see cref="Models.ServiceProviderIds"/>.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Rate-limit and retry settings for this provider.</summary>
    public required ChannelPolicy Channel { get; init; }

    /// <summary>Circuit-breaker configuration for this provider.</summary>
    public CircuitBreakerSettings CircuitBreaker { get; init; } = new();
}

/// <summary>
///     Conservative defaults: trip after a small number of consecutive failures, stay open
///     for several minutes before allowing a half-open trial. Tuned for "don't get our key
///     blocked" rather than "maximize uptime".
/// </summary>
public sealed record CircuitBreakerSettings
{
    /// <summary>
    ///     Minimum number of calls within the sampling window required before the breaker
    ///     evaluates the failure ratio. Set low to trip aggressively on consecutive failures.
    /// </summary>
    public int MinimumThroughput { get; init; } = 3;

    /// <summary>Window over which failure ratio is computed.</summary>
    public TimeSpan SamplingDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Failure ratio that trips the breaker (0.0–1.0).</summary>
    public double FailureRatio { get; init; } = 1.0;

    /// <summary>How long the breaker stays open before allowing a half-open trial.</summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromMinutes(10);
}
