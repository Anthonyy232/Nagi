using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;

namespace Nagi.Core.Http.Pipelines;

/// <summary>
///     Default <see cref="IProviderPipelineProvider"/>. Builds one circuit-breaker pipeline
///     per provider (shared across channels) and one rate-limit + retry pipeline per
///     (provider, channel). Each <see cref="ExecuteAsync"/> call layers them: the request
///     enters the channel pipeline first, then the breaker, then the actual HTTP call —
///     so retry attempts pass through the breaker individually.
/// </summary>
internal sealed class ProviderPipelineProvider : IProviderPipelineProvider, IAsyncDisposable
{
    private readonly ILogger<ProviderPipelineProvider> _logger;
    private readonly Dictionary<string, ProviderEntry> _providers;
    // TokenBucketRateLimiter owns a background replenishment timer; we must dispose every
    // instance we created or the timers leak past the singleton's lifetime.
    private readonly List<TokenBucketRateLimiter> _rateLimiters = new();

    public ProviderPipelineProvider(
        IEnumerable<ProviderPolicy> policies,
        ILogger<ProviderPipelineProvider> logger)
    {
        _logger = logger;
        _providers = policies.ToDictionary(p => p.ProviderId, BuildEntry, StringComparer.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var limiter in _rateLimiters)
        {
            await limiter.DisposeAsync().ConfigureAwait(false);
        }
        _rateLimiters.Clear();
    }

    public ValueTask<HttpResponseMessage> ExecuteAsync(
        string providerId,
        Func<CancellationToken, ValueTask<HttpResponseMessage>> httpCall,
        CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(providerId, out var entry))
            throw new InvalidOperationException(
                $"Provider '{providerId}' is not registered with the pipeline provider.");

        // Layer the channel pipeline (retry/concurrency/rate-limit) over the breaker so the
        // breaker sees every individual attempt the retry strategy makes, not just the
        // aggregated outcome the caller observes. Without this ordering a 3-attempt retry
        // sequence ending in success registers as a single success on the breaker, and one
        // ending in failure registers as a single failure — both delay tripping by the full
        // backoff window. With the breaker innermost, each attempt's outcome counts, and a
        // tripped breaker short-circuits subsequent retries via BrokenCircuitException
        // (which the retry predicate doesn't handle, so retry stops immediately).
        return entry.ChannelPipeline.ExecuteAsync(
            static async (state, ct) =>
            {
                var (breakerPipeline, httpCall) = state;
                return await breakerPipeline.ExecuteAsync(httpCall, ct).ConfigureAwait(false);
            },
            (entry.CircuitBreakerPipeline, httpCall),
            cancellationToken);
    }

    public bool IsCircuitOpen(string providerId)
        => _providers.TryGetValue(providerId, out var entry)
           && entry.CircuitBreakerStateProvider.CircuitState
               is CircuitState.Open or CircuitState.Isolated;

    public ProviderPolicy? GetPolicy(string providerId)
        => _providers.TryGetValue(providerId, out var entry) ? entry.Policy : null;

    private ProviderEntry BuildEntry(ProviderPolicy policy)
    {
        // CircuitBreakerStateProvider lets us peek at the breaker's state for IsCircuitOpen.
        var stateProvider = new CircuitBreakerStateProvider();

        var breakerPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                MinimumThroughput = policy.CircuitBreaker.MinimumThroughput,
                SamplingDuration = policy.CircuitBreaker.SamplingDuration,
                FailureRatio = policy.CircuitBreaker.FailureRatio,
                BreakDuration = policy.CircuitBreaker.BreakDuration,
                ShouldHandle = TransientNetworkPredicate()
                    .HandleResult(static r => IsCircuitTrippingFailure(r)),
                StateProvider = stateProvider,
                OnOpened = args =>
                {
                    _logger.LogWarning(
                        "Circuit breaker for provider '{ProviderId}' OPENED for {BreakDuration}. Outcome: {Outcome}",
                        policy.ProviderId, args.BreakDuration, DescribeOutcome(args.Outcome));
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    _logger.LogInformation(
                        "Circuit breaker for provider '{ProviderId}' CLOSED.", policy.ProviderId);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    _logger.LogInformation(
                        "Circuit breaker for provider '{ProviderId}' HALF-OPEN; trying one request.",
                        policy.ProviderId);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

        var channelPipeline = BuildChannelPipeline(policy.ProviderId, policy.Channel);
        return new ProviderEntry(policy, breakerPipeline, stateProvider, channelPipeline);
    }

    private ResiliencePipeline<HttpResponseMessage> BuildChannelPipeline(
        string providerId, ChannelPolicy channelPolicy)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();

        // Polly's retry strategy requires MaxRetryAttempts >= 1. Skip the strategy entirely
        // when retries are disabled — useful for tests and for providers we don't want to
        // hammer on retry (e.g., expensive 1-RPS APIs).
        if (channelPolicy.MaxRetries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = channelPolicy.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = channelPolicy.BaseRetryDelay,
                MaxDelay = channelPolicy.MaxRetryDelay,
                ShouldHandle = TransientNetworkPredicate()
                    .HandleResult(static r => IsRetryableStatus(r.StatusCode)),
                DelayGenerator = args =>
                {
                    // 429 with a Retry-After header: honor it, clamped by MaxRetryDelay.
                    if (args.Outcome.Result is { } resp &&
                        resp.StatusCode == HttpStatusCode.TooManyRequests &&
                        TryGetRetryAfter(resp, out var retryAfter))
                    {
                        var clamped = retryAfter > channelPolicy.MaxRetryDelay
                            ? channelPolicy.MaxRetryDelay
                            : retryAfter;
                        return ValueTask.FromResult<TimeSpan?>(clamped);
                    }
                    // Fall through to Polly's default exponential-with-jitter calculation.
                    return ValueTask.FromResult<TimeSpan?>(null);
                },
                OnRetry = args =>
                {
                    _logger.LogDebug(
                        "Retry {Attempt}/{Max} for provider '{ProviderId}'. Delay: {Delay}. Outcome: {Outcome}",
                        args.AttemptNumber + 1, channelPolicy.MaxRetries,
                        providerId, args.RetryDelay,
                        DescribeOutcome(args.Outcome));
                    // Polly hands the final outcome to the caller but keeps no reference to
                    // intermediate failure responses. Dispose them here so connections aren't
                    // pinned until GC runs.
                    args.Outcome.Result?.Dispose();
                    return ValueTask.CompletedTask;
                },
            });
        }

        // Concurrency cap. Holds a slot for the duration of the call.
        builder.AddConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = channelPolicy.MaxConcurrent,
            QueueLimit = int.MaxValue,
        });

        // Rate limit (token bucket). Refills PermitsPerWindow tokens every Window.
        // The limiter is created once per pipeline and reused across calls — Polly invokes
        // the RateLimiter delegate on every Execute, so creating a new TokenBucketRateLimiter
        // each time would defeat the rate limit (every call would see a fresh full bucket).
        var permits = Math.Max(1, channelPolicy.PermitsPerWindow);
        var tokenBucket = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = permits,
            TokensPerPeriod = permits,
            ReplenishmentPeriod = channelPolicy.Window,
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
        _rateLimiters.Add(tokenBucket);

        builder.AddRateLimiter(new RateLimiterStrategyOptions
        {
            RateLimiter = args => tokenBucket.AcquireAsync(permitCount: 1, args.Context.CancellationToken),
        });

        return builder.Build();
    }

    /// <summary>
    ///     Exception types treated as transient by both the retry strategy and the circuit
    ///     breaker. Kept in one place so the two predicates can never silently diverge.
    /// </summary>
    private static PredicateBuilder<HttpResponseMessage> TransientNetworkPredicate()
        => new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<IOException>()
            .Handle<System.Net.Sockets.SocketException>();

    private static bool IsRetryableStatus(HttpStatusCode statusCode)
        => HttpStatusClassification.IsTransient(statusCode);

    /// <summary>
    ///     Statuses that count toward the breaker. Adds 401/403 to the transient set so auth
    ///     issues trip the breaker quickly (we want to bail when blocked, not keep hammering).
    /// </summary>
    private static bool IsCircuitTrippingFailure(HttpResponseMessage response)
        => HttpStatusClassification.IsTransient(response.StatusCode)
           || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool TryGetRetryAfter(HttpResponseMessage response, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var header = response.Headers.RetryAfter;
        if (header is not null)
        {
            if (header.Delta is { } delta)
            {
                retryAfter = delta;
                return true;
            }
            if (header.Date is { } date)
            {
                var diff = date - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                {
                    retryAfter = diff;
                    return true;
                }
            }
        }

        // ListenBrainz uses X-RateLimit-Reset-In (seconds) instead of Retry-After.
        if (response.Headers.TryGetValues("X-RateLimit-Reset-In", out var values))
        {
            foreach (var v in values)
            {
                if (int.TryParse(v, out var seconds) && seconds > 0)
                {
                    retryAfter = TimeSpan.FromSeconds(seconds);
                    return true;
                }
            }
        }

        return false;
    }

    private static string DescribeOutcome(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is { } ex) return $"exception={ex.GetType().Name}";
        if (outcome.Result is { } result) return $"status={(int)result.StatusCode} {result.StatusCode}";
        return "no result";
    }

    private sealed record ProviderEntry(
        ProviderPolicy Policy,
        ResiliencePipeline<HttpResponseMessage> CircuitBreakerPipeline,
        CircuitBreakerStateProvider CircuitBreakerStateProvider,
        ResiliencePipeline<HttpResponseMessage> ChannelPipeline);
}
