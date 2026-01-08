using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Http;

namespace Nagi.Core.Services.Implementations;

// Private DTO for deserializing the API key from the server response.
file class ApiKeyResponse
{
    public string? Value { get; set; }
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
            ServiceProviderIds.TheAudioDb => "theaudiodb-key",
            ServiceProviderIds.Spotify => "spotify-key",
            ServiceProviderIds.LastFm => "lastfm-key",
            ServiceProviderIds.LastFmSecret => "lastfm-secret-key",
            ServiceProviderIds.FanartTv => "fanarttv-key",
            _ => throw new ArgumentException($"Unknown service: {keyName}", nameof(keyName))
        };

        var requestUri = $"{serverUrl.TrimEnd('/')}/api/{route}";
        var operationName = $"API key fetch for {keyName}";
        using var httpClient = _httpClientFactory.CreateClient();

        return await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Add("X-API-KEY", serverKey);

                if (!string.IsNullOrEmpty(subscriptionKey))
                    request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

                _logger.LogDebug("Fetching API key '{ApiKeyName}' from server (Attempt {Attempt}/3)", keyName, attempt);

                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var apiKeyResponse = JsonSerializer.Deserialize<ApiKeyResponse>(jsonContent, options);
                    var key = apiKeyResponse?.Value;

                    if (string.IsNullOrWhiteSpace(key))
                    {
                        _logger.LogError("API key response for '{ApiKeyName}' is invalid (missing 'Value').", keyName);
                        return RetryResult<string>.PermanentFailure();
                    }

                    return RetryResult<string>.Success(key);
                }

                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _globalAuthFailed = true;
                    _logger.LogCritical(
                        "Authentication failed for Nagi API Server. All future requests disabled. Status: {StatusCode}. Response: {ErrorContent}",
                        response.StatusCode, errorContent);
                    return RetryResult<string>.PermanentFailure();
                }

                _logger.LogError("Error fetching API key '{ApiKeyName}'. Status: {StatusCode}. Response: {ErrorContent}",
                    keyName, response.StatusCode, errorContent);

                if (HttpRetryHelper.IsRetryableStatusCode(response.StatusCode))
                    return RetryResult<string>.TransientFailure();

                return RetryResult<string>.PermanentFailure();
            },
            _logger,
            operationName,
            cancellationToken,
            3
        ).ConfigureAwait(false);
    }
}