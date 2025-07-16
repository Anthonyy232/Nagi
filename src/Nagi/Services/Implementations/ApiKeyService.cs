using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Nagi.Services.Abstractions;

namespace Nagi.Services;

/// <summary>
/// Manages the retrieval and caching of API keys from a secure server endpoint.
/// This implementation is thread-safe using a ConcurrentDictionary.
/// </summary>
public class ApiKeyService : IApiKeyService {
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _cachedApiKeys = new();
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public ApiKeyService(IHttpClientFactory httpClientFactory, IConfiguration configuration) {
        _httpClient = httpClientFactory.CreateClient();
        _configuration = configuration;
    }

    /// <inheritdoc />
    public Task<string?> GetApiKeyAsync(string keyName, CancellationToken cancellationToken = default) {
        // GetOrAdd ensures the factory function is only ever executed once for a given key,
        // even under concurrent access. The Lazy<T> wrapper handles the caching of the task result.
        var lazyTask = _cachedApiKeys.GetOrAdd(keyName,
            _ => new Lazy<Task<string?>>(() => FetchKeyFromServerAsync(keyName, cancellationToken)));

        return lazyTask.Value;
    }

    /// <inheritdoc />
    public async Task<string?> RefreshApiKeyAsync(string keyName, CancellationToken cancellationToken = default) {
        // Invalidate the cached entry. The next call to GetApiKeyAsync will trigger a new fetch.
        _cachedApiKeys.TryRemove(keyName, out _);
        return await GetApiKeyAsync(keyName, cancellationToken);
    }

    /// <summary>
    /// Performs the actual HTTP request to fetch an API key from the server.
    /// </summary>
    private async Task<string?> FetchKeyFromServerAsync(string keyName, CancellationToken cancellationToken) {
        var serverUrl = _configuration["NagiApiServer:Url"];
        var serverKey = _configuration["NagiApiServer:ApiKey"];
        var subscriptionKey = _configuration["NagiApiServer:SubscriptionKey"];

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(serverKey)) {
            Debug.WriteLine("CRITICAL: Nagi API Server URL or ApiKey is not configured.");
            return null;
        }

        var endpoint = $"{serverUrl.TrimEnd('/')}/api/{keyName}-key";

        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("X-API-KEY", serverKey);

            if (!string.IsNullOrEmpty(subscriptionKey))
                request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode) {
                Debug.WriteLine(
                    $"Error fetching API key '{keyName}'. Status: {response.StatusCode}, Endpoint: {endpoint}");
                return null;
            }

            var key = await response.Content.ReadAsStringAsync(cancellationToken);
            return key.Trim().Trim('"');
        }
        catch (OperationCanceledException) {
            throw; // Re-throw cancellation exceptions to be handled by the caller.
        }
        catch (Exception ex) {
            Debug.WriteLine($"Exception while fetching API key '{keyName}': {ex.Message}");
            return null;
        }
    }
}