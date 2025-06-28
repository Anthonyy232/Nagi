using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Nagi.Services.Abstractions;

namespace Nagi.Services {
    /// <summary>
    /// Manages the retrieval and caching of the Last.fm API key from a secure server.
    /// </summary>
    public class ApiKeyService : IApiKeyService {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private string? _cachedLastFmKey;

        // A semaphore ensures thread-safe fetching of the API key on the first request.
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public ApiKeyService(IHttpClientFactory httpClientFactory, IConfiguration configuration) {
            _httpClient = httpClientFactory.CreateClient();
            _configuration = configuration;
        }

        public async Task<string?> GetLastFmApiKeyAsync(CancellationToken cancellationToken = default) {
            // Return the cached key immediately if it's available.
            if (!string.IsNullOrEmpty(_cachedLastFmKey)) {
                return _cachedLastFmKey;
            }

            await _semaphore.WaitAsync(cancellationToken);
            try {
                // Double-check if another thread fetched the key while this one was waiting.
                if (!string.IsNullOrEmpty(_cachedLastFmKey)) {
                    return _cachedLastFmKey;
                }

                return await FetchAndCacheKeyAsync(cancellationToken);
            }
            finally {
                _semaphore.Release();
            }
        }

        public async Task<string?> RefreshApiKeyAsync(CancellationToken cancellationToken = default) {
            await _semaphore.WaitAsync(cancellationToken);
            try {
                Debug.WriteLine("Invalidating cached API key and forcing refresh.");
                // Invalidate the cache and re-fetch the key.
                _cachedLastFmKey = null;
                return await FetchAndCacheKeyAsync(cancellationToken);
            }
            finally {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Fetches the API key from the configured server endpoint.
        /// This method should only be called from within a semaphore lock to prevent race conditions.
        /// </summary>
        private async Task<string?> FetchAndCacheKeyAsync(CancellationToken cancellationToken) {
            var serverUrl = _configuration["NagiApiServer:Url"];
            var serverKey = _configuration["NagiApiServer:ApiKey"];
            var subscriptionKey = _configuration["NagiApiServer:SubscriptionKey"];

            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(serverKey) || string.IsNullOrEmpty(subscriptionKey)) {
                Debug.WriteLine("CRITICAL: Nagi API Server URL, ApiKey, or SubscriptionKey is not configured.");
                return null;
            }

            try {
                using var request = new HttpRequestMessage(HttpMethod.Get, serverUrl);
                request.Headers.Add("X-API-KEY", serverKey);
                request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode) {
                    Debug.WriteLine($"Error fetching API key. Status: {response.StatusCode}");
                    return null;
                }

                var key = await response.Content.ReadAsStringAsync(cancellationToken);
                // The key may be returned with surrounding quotes, which should be removed.
                _cachedLastFmKey = key.Trim().Trim('"');
                Debug.WriteLine("Successfully fetched and cached Last.fm API key.");
                return _cachedLastFmKey;
            }
            catch (OperationCanceledException) {
                // This is an expected exception, no need for an error log.
                throw;
            }
            catch (Exception ex) {
                Debug.WriteLine($"Exception while fetching API key: {ex.Message}");
                return null;
            }
        }
    }
}