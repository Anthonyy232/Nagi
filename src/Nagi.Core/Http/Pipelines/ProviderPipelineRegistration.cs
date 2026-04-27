using Microsoft.Extensions.DependencyInjection;

namespace Nagi.Core.Http.Pipelines;

/// <summary>
///     DI registration helpers for <see cref="IProviderPipelineProvider"/>. Provider services
///     declare their rate-limit / retry / breaker policy here, in one place, instead of each
///     service managing its own static semaphore and retry helper.
/// </summary>
public static class ProviderPipelineRegistration
{
    /// <summary>
    ///     Registers the provider-pipeline infrastructure and lets the caller declare each
    ///     provider's policy through a builder. Re-registering the same provider id replaces
    ///     the prior policy.
    /// </summary>
    public static IServiceCollection AddProviderPipelines(
        this IServiceCollection services,
        Action<ProviderPipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ProviderPipelineBuilder();
        configure(builder);
        var policies = builder.Build();

        // The provider list is captured at DI-build time. The pipeline provider is a
        // singleton because the rate limiters and circuit-breaker state must be shared
        // process-wide; one instance per scope would defeat the purpose.
        services.AddSingleton<IProviderPipelineProvider>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProviderPipelineProvider>>();
            return new ProviderPipelineProvider(policies, logger);
        });

        return services;
    }
}

/// <summary>
///     Fluent builder for declaring provider policies. Intentionally minimal: the per-provider
///     <see cref="ProviderPolicy"/> record carries all the settings; this just collects them.
/// </summary>
public sealed class ProviderPipelineBuilder
{
    private readonly Dictionary<string, ProviderPolicy> _providers = new(StringComparer.Ordinal);

    /// <summary>
    ///     Registers a provider policy. The <paramref name="policy"/>'s <c>ProviderId</c>
    ///     determines the lookup key.
    /// </summary>
    public ProviderPipelineBuilder AddProvider(ProviderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.ProviderId))
            throw new ArgumentException("ProviderPolicy.ProviderId must be set.", nameof(policy));

        _providers[policy.ProviderId] = policy;
        return this;
    }

    internal IEnumerable<ProviderPolicy> Build() => _providers.Values;
}
