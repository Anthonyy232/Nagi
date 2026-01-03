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
///     Manages the retrieval and thread-safe caching of API keys from a secure server endpoint.
/// </summary>
public class ApiKeyService : IApiKeyService, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cachedApiKeys = new();
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiKeyService> _logger;
    private bool _isDisposed;

    public ApiKeyService(IHttpClientFactory httpClientFactory, IConfiguration configuration,
        ILogger<ApiKeyService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetApiKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        // GetOrAdd ensures the factory function is only executed once for a given key.
        // The Lazy<Task<T>> wrapper caches the task, so the fetch operation only runs once.
        // IMPORTANT: We use CancellationToken.None for the fetch to avoid caching cancelled tasks.
        var lazyTask = _cachedApiKeys.GetOrAdd(keyName,
            _ => new Lazy<Task<string?>>(() => FetchKeyFromServerAsync(keyName, CancellationToken.None)));

        try
        {
            // Use WaitAsync to respect the caller's cancellation token without affecting the cached task.
            return await lazyTask.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Let cancellation propagate normally; caller requested cancellation.
            throw;
        }
        catch (Exception)
        {
            // If the underlying task failed, remove it from cache so subsequent calls can retry.
            // Return null to allow callers to handle the failure gracefully.
            _cachedApiKeys.TryRemove(keyName, out _);
            return null;
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
    ///     Releases the resources used by the <see cref="ApiKeyService" />.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        _httpClient.Dispose();
        _isDisposed = true;
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
            throw new InvalidOperationException("Nagi API Server configuration is missing.");
        }

        // Ensure the URL has a scheme to prevent URI format exceptions.
        if (!serverUrl.StartsWith("http://") && !serverUrl.StartsWith("https://")) serverUrl = "https://" + serverUrl;

        var endpoint = $"{serverUrl.TrimEnd('/')}/api/{keyName}-key";
        _logger.LogDebug("Fetching API key '{ApiKeyName}' from server: {Endpoint}", keyName, endpoint);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("X-API-KEY", serverKey);

            if (!string.IsNullOrEmpty(subscriptionKey))
                request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Error fetching API key '{ApiKeyName}'. Status: {StatusCode}. Response: {ErrorContent}",
                    keyName, response.StatusCode, errorContent);
                throw new HttpRequestException($"Failed to fetch API key '{keyName}'. Status: {response.StatusCode}");
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var apiKeyResponse = JsonSerializer.Deserialize<ApiKeyResponse>(jsonContent, options);

            var finalKey = apiKeyResponse?.Value;

            if (string.IsNullOrWhiteSpace(finalKey))
            {
                _logger.LogError(
                    "API key response for '{ApiKeyName}' is invalid. It is missing the 'Value' field or is empty.",
                    keyName);
                throw new InvalidOperationException($"API key response for '{keyName}' is invalid.");
            }

            _logger.LogDebug("Successfully fetched API key '{ApiKeyName}'.", keyName);
            return finalKey;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Fetching API key '{ApiKeyName}' was canceled.", keyName);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An exception occurred while fetching API key '{ApiKeyName}'.", keyName);
            throw;
        }
    }
}