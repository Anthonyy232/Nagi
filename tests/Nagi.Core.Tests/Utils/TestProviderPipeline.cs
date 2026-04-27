using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Http.Pipelines;

namespace Nagi.Core.Tests;

/// <summary>
///     Builds a fast, no-retry <see cref="ProviderPipelineProvider"/> for a given provider id
///     so unit tests don't pay real-world rate-limit or backoff delays.
/// </summary>
internal static class TestProviderPipeline
{
    public static ProviderPipelineProvider Build(string providerId, int retries = 0) =>
        new(
            new[]
            {
                new ProviderPolicy
                {
                    ProviderId = providerId,
                    Channel = new ChannelPolicy
                    {
                        PermitsPerWindow = 1000,
                        Window = TimeSpan.FromSeconds(1),
                        MaxConcurrent = 4,
                        MaxRetries = retries,
                        BaseRetryDelay = TimeSpan.FromMilliseconds(20),
                        MaxRetryDelay = TimeSpan.FromMilliseconds(100),
                    },
                },
            },
            NullLogger<ProviderPipelineProvider>.Instance);
}
