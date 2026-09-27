using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nagi.Core.Http.Pipelines;

public static class ProviderPipelineRegistration
{
    public static IServiceCollection AddProviderPipelines(
        this IServiceCollection services,
        IEnumerable<ProviderPolicy> policies)
    {
        // Rate limits and breaker state must be shared across service scopes.
        return services.AddSingleton<IProviderPipelineProvider>(sp =>
            new ProviderPipelineProvider(policies, sp.GetRequiredService<ILogger<ProviderPipelineProvider>>()));
    }
}
