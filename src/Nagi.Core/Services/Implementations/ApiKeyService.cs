using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

file class ApiKeyResponse
{
    public string? Value { get; set; }
}

/// <summary>
///     Manages the retrieval and thread-safe caching of API keys from a secure server endpoint.
/// </summary>
public class ApiKeyService : IApiKeyService
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cachedApiKeys = new();
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<ApiKeyService> _logger;
    private volatile bool _globalAuthFailed;

    public ApiKeyService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        IConfiguration configuration,
        ILogger<ApiKeyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _pipelines = pipelines;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetApiKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        if (_globalAuthFailed)
        {
            return null;
        }

        // Lazy coalesces concurrent callers even when GetOrAdd invokes its factory more than once.
        var lazyTask = _cachedApiKeys.GetOrAdd(keyName,
            _ => new Lazy<Task<string?>>(() => FetchKeyFromServerAsync(keyName, CancellationToken.None)));

        var result = await lazyTask.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            // Preserve a newer value installed by a concurrent forced refresh.
            _cachedApiKeys.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(keyName, lazyTask));
        }
        return result;
    }

    /// <inheritdoc />
    public Task<string?> RefreshApiKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Forcing refresh for API key '{ApiKeyName}'.", keyName);
        _cachedApiKeys.TryRemove(keyName, out _);
        return GetApiKeyAsync(keyName, cancellationToken);
    }

    /// <summary>
    ///     Performs the HTTP request to fetch an API key from the configured server.
    /// </summary>
    private async Task<string?> FetchKeyFromServerAsync(string keyName, CancellationToken cancellationToken)
    {
        var serverUrl = _configuration["NagiApiServer:Url"];
        var serverKey = _configuration["NagiApiServer:ApiKey"];
        var subscriptionKey = _configuration["NagiApiServer:SubscriptionKey"];

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(serverKey))
        {
            _logger.LogCritical("Nagi API Server URL or ApiKey is not configured. API key retrieval will fail.");
            return null;
        }

        var route = keyName switch
        {
            ServiceProviderIds.TheAudioDb => "theaudiodb-key",
            ServiceProviderIds.LastFm => "lastfm-key",
            ServiceProviderIds.LastFmSecret => "lastfm-secret-key",
            ServiceProviderIds.FanartTv => "fanarttv-key",
            _ => throw new ArgumentException($"Unknown service: {keyName}", nameof(keyName))
        };

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri) ||
            serverUri.Scheme is not ("http" or "https"))
            serverUri = new Uri("https://" + serverUrl);
        var requestUri = new UriBuilder(serverUri)
        {
            Path = $"{serverUri.AbsolutePath.TrimEnd('/')}/api/{route}"
        }.Uri;
        using var httpClient = _httpClientFactory.CreateClient();

        return await _pipelines.ExecuteWithFallbackAsync<string?>(
            ServiceProviderIds.NagiApi,
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Add("X-API-KEY", serverKey);
                if (!string.IsNullOrEmpty(subscriptionKey))
                    request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
                _logger.LogDebug("Fetching API key '{ApiKeyName}' from server.", keyName);
                return await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (response.IsSuccessStatusCode)
                {
                    var apiKeyResponse = await response.Content.ReadFromJsonAsync<ApiKeyResponse>(cancellationToken: ct)
                        .ConfigureAwait(false);
                    var key = apiKeyResponse?.Value;

                    if (string.IsNullOrWhiteSpace(key))
                    {
                        _logger.LogError("API key response for '{ApiKeyName}' is invalid (missing 'Value').", keyName);
                        return null;
                    }
                    return key;
                }

                var errorContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    _globalAuthFailed = true;
                    _logger.LogCritical(
                        "Authentication failed for Nagi API Server. All future requests disabled. Status: {StatusCode}. Response: {ErrorContent}",
                        response.StatusCode, errorContent);
                    return null;
                }

                _logger.LogError("Error fetching API key '{ApiKeyName}'. Status: {StatusCode}. Response: {ErrorContent}",
                    keyName, response.StatusCode, errorContent);
                return null;
            },
            fallback: null,
            _logger,
            $"fetch API key '{keyName}'",
            cancellationToken).ConfigureAwait(false);
    }
}
