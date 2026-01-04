using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

// Private DTO for deserializing the API key from the server response.
file class ApiKeyResponse
{
    public string? Value { get; set; }
}

/// <summary>
///     Well-known service names for API key retrieval.
/// </summary>
public static class ApiKeyServices
{
    public const string TheAudioDb = "theaudiodb";
    public const string Spotify = "spotify";
    public const string LastFm = "lastfm";
    public const string LastFmSecret = "lastfm-secret";
    public const string FanartTv = "fanarttv";
}

/// <summary>
///     Manages the retrieval and thread-safe caching of API keys from a secure server endpoint.
/// </summary>
public class ApiKeyService : IApiKeyService, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cachedApiKeys = new();
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApiKeyService> _logger;
    private volatile bool _globalAuthFailed;

    public ApiKeyService(IHttpClientFactory httpClientFactory, IConfiguration configuration,
        ILogger<ApiKeyService> logger)
    {
        _httpClientFactory = httpClientFactory;
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

        // GetOrAdd ensures the factory function is only executed once for a given key.
        // The Lazy<Task<T>> wrapper caches the task, so the fetch operation only runs once.
        // We now cache failures (null) to prevent repeated attempts.
        var lazyTask = _cachedApiKeys.GetOrAdd(keyName,
            _ => new Lazy<Task<string?>>(() => FetchKeyFromServerAsync(keyName, CancellationToken.None)));

        try
        {
            // Use WaitAsync to respect the caller's cancellation token without affecting the cached task.
            return await lazyTask.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation requested by the caller.
            throw;
        }
    }

    /// <inheritdoc />
    public Task<string?> RefreshApiKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Forcing refresh for API key '{ApiKeyName}'.", keyName);
        _cachedApiKeys.TryRemove(keyName, out _);
        return GetApiKeyAsync(keyName, cancellationToken);
    }

    /// <summary>
    ///     No-op Dispose as IHttpClientFactory manages client lifetimes.
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Performs the HTTP request to fetch an API key from the configured server.
    /// </summary>
    private async Task<string?> FetchKeyFromServerAsync(string keyName, CancellationToken cancellationToken)
    {
        try
        {
            var serverUrl = _configuration["NagiApiServer:Url"];
            var serverKey = _configuration["NagiApiServer:ApiKey"];
            var subscriptionKey = _configuration["NagiApiServer:SubscriptionKey"];

            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(serverKey))
            {
                _logger.LogCritical("Nagi API Server URL or ApiKey is not configured. API key retrieval will fail.");
                return null;
            }

            // Ensure the URL has a scheme to prevent URI format exceptions.
            if (!serverUrl.StartsWith("http://") && !serverUrl.StartsWith("https://")) serverUrl = "https://" + serverUrl;

            // Build the request URL.
            var route = keyName switch
            {
                ApiKeyServices.TheAudioDb => "theaudiodb-key",
                ApiKeyServices.Spotify => "spotify-key",
                ApiKeyServices.LastFm => "lastfm-key",
                ApiKeyServices.LastFmSecret => "lastfm-secret-key",
                ApiKeyServices.FanartTv => "fanarttv-key",
                _ => throw new ArgumentException($"Unknown service: {keyName}", nameof(keyName))
            };

            var requestUri = $"{serverUrl.TrimEnd('/')}/api/{route}";
            _logger.LogDebug("Fetching API key '{ApiKeyName}' from server: {Endpoint}", keyName, requestUri);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("X-API-KEY", serverKey);

            if (!string.IsNullOrEmpty(subscriptionKey))
                request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

            using var httpClient = _httpClientFactory.CreateClient();
            var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _globalAuthFailed = true;
                    _logger.LogCritical(
                        "Authentication failed for Nagi API Server. API Key or Subscription Key is invalid. " +
                        "Status: {StatusCode}. Response: {ErrorContent}. All future API key requests for this session will be disabled.",
                        response.StatusCode, errorContent);
                }
                else
                {
                    _logger.LogError(
                        "Error fetching API key '{ApiKeyName}'. Status: {StatusCode}. Response: {ErrorContent}",
                        keyName, response.StatusCode, errorContent);
                }
                
                return null;
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var apiKeyResponse = JsonSerializer.Deserialize<ApiKeyResponse>(jsonContent, options);

            var finalKey = apiKeyResponse?.Value;

            if (string.IsNullOrWhiteSpace(finalKey))
            {
                _logger.LogError(
                    "API key response for '{ApiKeyName}' is invalid. It is missing the 'Value' field or is empty.",
                    keyName);
                return null;
            }

            _logger.LogDebug("Successfully fetched API key '{ApiKeyName}'.", keyName);
            return finalKey;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Fetching API key '{ApiKeyName}' was canceled.", keyName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An exception occurred while fetching API key '{ApiKeyName}'.", keyName);
            return null;
        }
    }
}