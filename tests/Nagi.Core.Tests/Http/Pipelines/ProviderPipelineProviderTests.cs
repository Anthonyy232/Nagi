using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Http.Pipelines;
using Polly.CircuitBreaker;
using Xunit;

namespace Nagi.Core.Tests.Http.Pipelines;

public class ProviderPipelineProviderTests
{
    // NullLogger avoids the NSubstitute strong-name proxy issue with internal generic types,
    // and we don't assert on log calls in these tests anyway.
    private readonly NullLogger<ProviderPipelineProvider> _logger = NullLogger<ProviderPipelineProvider>.Instance;

    private static ChannelPolicy ApiPolicy(int rps = 100, int concurrency = 8, int retries = 0) => new()
    {
        PermitsPerWindow = rps,
        MaxConcurrent = concurrency,
        MaxRetries = retries,
        BaseRetryDelay = TimeSpan.FromMilliseconds(20),
        MaxRetryDelay = TimeSpan.FromSeconds(1),
    };

    private static ProviderPolicy Policy(
        string id,
        ChannelPolicy? channel = null,
        CircuitBreakerSettings? breaker = null) => new()
        {
            ProviderId = id,
            Channel = channel ?? ApiPolicy(),
            CircuitBreaker = breaker ?? new CircuitBreakerSettings(),
        };

    private ProviderPipelineProvider Build(params ProviderPolicy[] policies)
        => new(policies, _logger);

    private static HttpResponseMessage Resp(HttpStatusCode code) => new(code);

    [Fact]
    public async Task ExecuteAsync_PassesThroughSuccessfulResponse()
    {
        var sut = Build(Policy("p"));

        var response = await sut.ExecuteAsync("p", ct => ValueTask.FromResult(Resp(HttpStatusCode.OK)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownProvider_Throws()
    {
        var sut = Build(Policy("p"));

        var act = async () => await sut.ExecuteAsync("nope", _ => ValueTask.FromResult(Resp(HttpStatusCode.OK)));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not registered*");
    }

    [Fact]
    public async Task RateLimiter_QueuesCallsBeyondPermitsPerSecond()
    {
        // 2 RPS, single bucket. Three calls should take ~1 second total: the third has to
        // wait for the bucket to refill (1s replenishment period).
        var sut = Build(Policy("p", channel: ApiPolicy(rps: 2, concurrency: 8)));

        var stopwatch = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 3).Select(_ => sut.ExecuteAsync("p", _ => ValueTask.FromResult(Resp(HttpStatusCode.OK))).AsTask()).ToArray();
        await Task.WhenAll(tasks);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(800),
            "the third request must wait for the rate-limit bucket to refill");
    }

    [Fact]
    public async Task ConcurrencyLimiter_BoundsInFlightRequests()
    {
        var inFlight = 0;
        var maxObserved = 0;
        var gate = new TaskCompletionSource();

        var sut = Build(Policy("p", channel: ApiPolicy(rps: 100, concurrency: 2)));

        var tasks = Enumerable.Range(0, 5).Select(_ => sut.ExecuteAsync("p", async ct =>
            {
                var current = Interlocked.Increment(ref inFlight);
                int observed;
                do
                {
                    observed = maxObserved;
                    if (current <= observed) break;
                } while (Interlocked.CompareExchange(ref maxObserved, current, observed) != observed);

                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                Interlocked.Decrement(ref inFlight);
                return Resp(HttpStatusCode.OK);
            }).AsTask()).ToArray();

        // Give the limiter a moment to admit the first batch, then release everything.
        await Task.Delay(150);
        gate.SetResult();
        await Task.WhenAll(tasks);

        maxObserved.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Retry_OnTransient5xx_RetriesAndEventuallySucceeds()
    {
        var attempt = 0;
        var sut = Build(Policy("p", channel: ApiPolicy(retries: 3)));

        var response = await sut.ExecuteAsync("p", _ =>
        {
            attempt++;
            return ValueTask.FromResult(attempt < 3
                ? Resp(HttpStatusCode.ServiceUnavailable)
                : Resp(HttpStatusCode.OK));
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        attempt.Should().Be(3);
    }

    [Fact]
    public async Task Retry_DoesNotRetryNonTransient4xx()
    {
        var attempt = 0;
        var sut = Build(Policy("p", channel: ApiPolicy(retries: 3)));

        // 404 isn't retryable; it should pass through after one call.
        var response = await sut.ExecuteAsync("p", _ =>
        {
            attempt++;
            return ValueTask.FromResult(Resp(HttpStatusCode.NotFound));
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        attempt.Should().Be(1);
    }

    [Fact]
    public async Task Retry_HonorsRetryAfterHeaderOn429()
    {
        var attempt = 0;
        var sut = Build(Policy("p", channel: ApiPolicy(retries: 1)));

        var stopwatch = Stopwatch.StartNew();
        var response = await sut.ExecuteAsync("p", _ =>
        {
            attempt++;
            if (attempt == 1)
            {
                var rateLimited = Resp(HttpStatusCode.TooManyRequests);
                rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(400));
                return ValueTask.FromResult(rateLimited);
            }
            return ValueTask.FromResult(Resp(HttpStatusCode.OK));
        });
        stopwatch.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(350),
            "the retry must wait at least the Retry-After value");
    }

    [Fact]
    public async Task Retry_HonorsXRateLimitResetInHeaderOn429()
    {
        // ListenBrainz returns X-RateLimit-Reset-In (seconds) instead of Retry-After.
        var attempt = 0;
        var sut = Build(Policy("p", channel: ApiPolicy(retries: 1)));

        var stopwatch = Stopwatch.StartNew();
        var response = await sut.ExecuteAsync("p", _ =>
        {
            attempt++;
            if (attempt == 1)
            {
                var rateLimited = Resp(HttpStatusCode.TooManyRequests);
                rateLimited.Headers.Add("X-RateLimit-Reset-In", "1");
                return ValueTask.FromResult(rateLimited);
            }
            return ValueTask.FromResult(Resp(HttpStatusCode.OK));
        });
        stopwatch.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(900),
            "the retry must wait at least the X-RateLimit-Reset-In value");
    }

    [Fact]
    public async Task CircuitBreaker_TripsAfterConsecutiveFailures()
    {
        // MinimumThroughput=3, FailureRatio=1.0 → 3 consecutive failures trips the breaker.
        // BreakDuration is long enough that the next call should be rejected immediately.
        var sut = Build(Policy("p", channel: ApiPolicy(retries: 0), breaker: new CircuitBreakerSettings
        {
            MinimumThroughput = 3,
            SamplingDuration = TimeSpan.FromSeconds(30),
            FailureRatio = 1.0,
            BreakDuration = TimeSpan.FromMinutes(5),
        }));

        for (var i = 0; i < 3; i++)
        {
            var response = await sut.ExecuteAsync("p", _ => ValueTask.FromResult(Resp(HttpStatusCode.InternalServerError)));
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        sut.IsCircuitOpen("p").Should().BeTrue();

        var act = async () => await sut.ExecuteAsync("p", _ => ValueTask.FromResult(Resp(HttpStatusCode.OK)));
        await act.Should().ThrowAsync<BrokenCircuitException>();
    }

    [Fact]
    public async Task CircuitBreaker_TripsOn401()
    {
        var sut = Build(Policy("p", channel: ApiPolicy(retries: 0), breaker: new CircuitBreakerSettings
        {
            MinimumThroughput = 3,
            SamplingDuration = TimeSpan.FromSeconds(30),
            FailureRatio = 1.0,
            BreakDuration = TimeSpan.FromMinutes(5),
        }));

        for (var i = 0; i < 3; i++)
        {
            await sut.ExecuteAsync("p", _ => ValueTask.FromResult(Resp(HttpStatusCode.Unauthorized)));
        }

        sut.IsCircuitOpen("p").Should().BeTrue();
    }

    [Fact]
    public async Task CircuitBreaker_IndependentAcrossDifferentProviders()
    {
        var sut = Build(
            Policy("p1", breaker: new CircuitBreakerSettings { MinimumThroughput = 3, FailureRatio = 1.0 }),
            Policy("p2", breaker: new CircuitBreakerSettings { MinimumThroughput = 3, FailureRatio = 1.0 }));

        for (var i = 0; i < 3; i++)
        {
            await sut.ExecuteAsync("p1", _ => ValueTask.FromResult(Resp(HttpStatusCode.InternalServerError)));
        }

        sut.IsCircuitOpen("p1").Should().BeTrue();
        sut.IsCircuitOpen("p2").Should().BeFalse();

        // p2 still works.
        var response = await sut.ExecuteAsync("p2", _ => ValueTask.FromResult(Resp(HttpStatusCode.OK)));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void GetPolicy_ReturnsRegisteredPolicy()
    {
        var policy = Policy("p");
        var sut = Build(policy);

        sut.GetPolicy("p").Should().BeSameAs(policy);
        sut.GetPolicy("missing").Should().BeNull();
    }

    [Fact]
    public async Task ExceptionFromHttpCall_BubblesAndCountsAsBreakerFailure()
    {
        var sut = Build(Policy("p", channel: ApiPolicy(retries: 0), breaker: new CircuitBreakerSettings
        {
            MinimumThroughput = 3,
            SamplingDuration = TimeSpan.FromSeconds(30),
            FailureRatio = 1.0,
            BreakDuration = TimeSpan.FromMinutes(5),
        }));

        for (var i = 0; i < 3; i++)
        {
            var act = async () => await sut.ExecuteAsync("p", _ => throw new HttpRequestException("network down"));
            await act.Should().ThrowAsync<HttpRequestException>();
        }

        sut.IsCircuitOpen("p").Should().BeTrue();
    }
}
