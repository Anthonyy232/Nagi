using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nagi.Core.OnlineProviders.Pipelines;

namespace Nagi.Core.OnlineProviders;

file class ApiKeyResponse
{
    public string? Value { get; set; }
}

/// <summary>
///     Fetches the providers' API keys from Nagi's API server, which serves each at <c>/api/{name}-key</c>.
/// </summary>
public class ApiKeyService : IApiKeyService
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cachedApiKeys = new();
    private readonly ProviderClient _client;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiKeyService> _logger;
    private volatile bool _globalAuthFailed;

    public ApiKeyService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        IConfiguration configuration,
        ILogger<ApiKeyService> logger)
    {
        _client = new ProviderClient(ProviderCatalog.NagiApi, httpClientFactory.CreateClient(), pipelines, logger);
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string?> GetApiKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        if (_globalAuthFailed)
        {
            return null;
        }

        // Lazy coalesces concurrent callers even when GetOrAdd invokes its factory more than once.
        var lazyTask = _cachedApiKeys.GetOrAdd(keyName,
            _ => new Lazy<Task<string?>>(() => FetchKeyFromServerAsync(keyName)));

        var result = await lazyTask.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            // Preserve a newer value installed by a concurrent forced refresh.
            _cachedApiKeys.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(keyName, lazyTask));
        }
        return result;
    }

    public Task<string?> RefreshApiKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Forcing refresh for API key '{ApiKeyName}'.", keyName);
        _cachedApiKeys.TryRemove(keyName, out _);
        return GetApiKeyAsync(keyName, cancellationToken);
    }

    private async Task<string?> FetchKeyFromServerAsync(string keyName)
    {
        var serverUrl = _configuration["NagiApiServer:Url"];
        var serverKey = _configuration["NagiApiServer:ApiKey"];
        var subscriptionKey = _configuration["NagiApiServer:SubscriptionKey"];

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(serverKey))
        {
            _logger.LogCritical("Nagi API Server URL or ApiKey is not configured. API key retrieval will fail.");
            return null;
        }

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri) ||
            serverUri.Scheme is not ("http" or "https"))
            serverUri = new Uri("https://" + serverUrl);
        var requestUri = new UriBuilder(serverUri)
        {
            Path = $"{serverUri.AbsolutePath.TrimEnd('/')}/api/{keyName}-key"
        }.Uri;

        // One fetch serves every caller, so no single caller can cancel it.
        var result = await _client.SendAsync<string>(_ =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Add("X-API-KEY", serverKey);
                if (!string.IsNullOrEmpty(subscriptionKey))
                    request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
                return request;
            },
            async (response, ct) =>
            {
                if (response.IsSuccessStatusCode)
                {
                    var apiKeyResponse = await response.Content.ReadFromJsonAsync<ApiKeyResponse>(ct).ConfigureAwait(false);
                    var key = apiKeyResponse?.Value;

                    if (string.IsNullOrWhiteSpace(key))
                    {
                        _logger.LogError("API key response for '{ApiKeyName}' is invalid (missing 'Value').", keyName);
                        return ServiceResult<string>.FromSuccessNotFound();
                    }
                    return ServiceResult<string>.FromSuccess(key);
                }

                var errorContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _globalAuthFailed = true;
                    _logger.LogCritical(
                        "Authentication failed for Nagi API Server. All future requests disabled. Status: {StatusCode}. Response: {ErrorContent}",
                        response.StatusCode, errorContent);
                    return ServiceResult<string>.FromTemporaryError();
                }

                _logger.LogError("Error fetching API key '{ApiKeyName}'. Status: {StatusCode}. Response: {ErrorContent}",
                    keyName, response.StatusCode, errorContent);
                return ServiceResult<string>.FromTemporaryError();
            },
            $"API key '{keyName}'",
            CancellationToken.None).ConfigureAwait(false);
        return result.Data;
    }
}
