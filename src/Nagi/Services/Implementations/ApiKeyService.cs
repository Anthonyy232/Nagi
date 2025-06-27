using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Nagi.Services.Abstractions;

namespace Nagi.Services {
    public class ApiKeyService : IApiKeyService {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private string? _cachedLastFmKey;

        // Use a SemaphoreSlim to prevent race conditions if multiple threads
        // try to fetch the key simultaneously.
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public ApiKeyService(IHttpClientFactory httpClientFactory, IConfiguration configuration) {
            _httpClient = httpClientFactory.CreateClient();
            _configuration = configuration;
        }

        public async Task<string?> GetLastFmApiKeyAsync(CancellationToken cancellationToken = default) {
            // Fast path: if the key is already cached, return it without locking.
            if (!string.IsNullOrEmpty(_cachedLastFmKey)) {
                return _cachedLastFmKey;
            }

            await _semaphore.WaitAsync(cancellationToken);
            try {
                // Double-check if another thread fetched the key while we were waiting.
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
                Debug.WriteLine("[ApiKeyService] Invalidating cached API key and forcing refresh.");
                _cachedLastFmKey = null; // Invalidate the cache
                return await FetchAndCacheKeyAsync(cancellationToken); // Re-fetch the key
            }
            finally {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Contains the actual logic to fetch the key from your server.
        /// This should only be called from within a semaphore lock.
        /// </summary>
        private async Task<string?> FetchAndCacheKeyAsync(CancellationToken cancellationToken) {
            // Read all three required values from configuration
            var serverUrl = _configuration["NagiApiServer:Url"];
            var serverKey = _configuration["NagiApiServer:ApiKey"];
            var subscriptionKey = _configuration["NagiApiServer:SubscriptionKey"];

            // Update the check to ensure all values are present
            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(serverKey) || string.IsNullOrEmpty(subscriptionKey))
            {
                Debug.WriteLine("[ApiKeyService] CRITICAL: NagiApiServer URL, ApiKey, or SubscriptionKey is not configured.");
                return null;
            }

            try {
                using var request = new HttpRequestMessage(HttpMethod.Get, serverUrl);

                request.Headers.Add("X-API-KEY", serverKey);
                request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode) {
                    var key = await response.Content.ReadAsStringAsync(cancellationToken);
                    // Trim whitespace and then quotes for robustness
                    _cachedLastFmKey = key.Trim().Trim('"');
                    Debug.WriteLine("[ApiKeyService] Successfully fetched and cached Last.fm API key.");
                    return _cachedLastFmKey;
                }

                Debug.WriteLine($"[ApiKeyService] Error fetching API key. Status: {response.StatusCode}");
                return null;
            }
            catch (OperationCanceledException) {
                Debug.WriteLine("[ApiKeyService] API key fetch was canceled.");
                return null;
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ApiKeyService] Exception while fetching API key: {ex.Message}");
                return null;
            }
        }
    }
}