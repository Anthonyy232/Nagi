using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Helpers;

internal static class ProviderRequests
{
    /// <summary>
    ///     Sends a GET through the provider's pipeline and maps the JSON body. A 404 means not found; any other
    ///     failure status, an open circuit, a transport error or a timeout is a retryable failure.
    /// </summary>
    public static async Task<ServiceResult<TResult>> GetJsonAsync<TResponse, TResult>(
        this IProviderPipelineProvider pipelines, HttpClient httpClient, string providerId, string url,
        Func<TResponse, ServiceResult<TResult>> map, ILogger logger, string operation, CancellationToken cancellationToken)
        where TResult : class
    {
        if (pipelines.IsCircuitOpen(providerId))
            return ServiceResult<TResult>.FromTemporaryError();

        return await pipelines.ExecuteWithFallbackAsync(
            providerId,
            async ct =>
            {
                logger.LogDebug("Requesting {Operation} from {ProviderId}", operation, providerId);
                return await httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return ServiceResult<TResult>.FromSuccessNotFound();

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("{ProviderId} returned {StatusCode} for {Operation}",
                        providerId, response.StatusCode, operation);
                    return ServiceResult<TResult>.FromTemporaryError();
                }

                var body = await response.Content.ReadFromJsonAsync<TResponse>(ct).ConfigureAwait(false);
                return body is null ? ServiceResult<TResult>.FromSuccessNotFound() : map(body);
            },
            fallback: ServiceResult<TResult>.FromTemporaryError(),
            logger,
            operation,
            cancellationToken).ConfigureAwait(false);
    }
}
