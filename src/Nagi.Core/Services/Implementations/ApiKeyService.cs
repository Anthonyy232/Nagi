using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

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
    private bool _isDisposed;

    public ApiKeyService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClient = httpClientFactory.CreateClient();
        _configuration = configuration;
    }

    /// <inheritdoc />
    public Task<string?> GetApiKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        // GetOrAdd ensures the factory function is only executed once for a given key.
        // The Lazy<Task<T>> wrapper caches the task, so the fetch operation only runs once.
        var lazyTask = _cachedApiKeys.GetOrAdd(keyName,
            _ => new Lazy<Task<string?>>(() => FetchKeyFromServerAsync(keyName, cancellationToken)));

        return lazyTask.Value;
    }

    /// <inheritdoc />
    public Task<string?> RefreshApiKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        Debug.WriteLine($"[ApiKeyService] Forcing refresh for API key '{keyName}'.");
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
            Debug.WriteLine("[ApiKeyService] CRITICAL: Nagi API Server URL or ApiKey is not configured.");
            return null;
        }

        // Ensure the URL has a scheme to prevent URI format exceptions.
        if (!serverUrl.StartsWith("http://") && !serverUrl.StartsWith("https://")) serverUrl = "https://" + serverUrl;

        var endpoint = $"{serverUrl.TrimEnd('/')}/api/{keyName}-key";

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
                Debug.WriteLine(
                    $"[ApiKeyService] Error fetching API key '{keyName}'. Status: {response.StatusCode}. Response: {errorContent}");
                return null;
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var apiKeyResponse = JsonSerializer.Deserialize<ApiKeyResponse>(jsonContent, options);

            var finalKey = apiKeyResponse?.Value;

            if (string.IsNullOrEmpty(finalKey))
            {
                Debug.WriteLine(
                    $"[ApiKeyService] Error: API key response for '{keyName}' is missing the 'Value' field.");
                return null;
            }

            Debug.WriteLine($"[ApiKeyService] Successfully fetched API key '{keyName}'.");
            return finalKey;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[ApiKeyService] Fetching API key '{keyName}' was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiKeyService] Exception while fetching API key '{keyName}': {ex.Message}");
            return null;
        }
    }
}