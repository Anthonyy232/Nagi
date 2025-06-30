using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Nagi.Services.Abstractions;

namespace Nagi.Services;

/// <summary>
///     Manages the retrieval and caching of API keys from a secure server endpoint. This service is thread-safe.
/// </summary>
public class ApiKeyService : IApiKeyService
{
    private readonly Dictionary<string, string?> _cachedApiKeys = new();
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public ApiKeyService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClient = httpClientFactory.CreateClient();
        _configuration = configuration;
    }

    public async Task<string?> GetApiKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        if (_cachedApiKeys.TryGetValue(keyName, out var cachedKey) && !string.IsNullOrEmpty(cachedKey))
            return cachedKey;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_cachedApiKeys.TryGetValue(keyName, out cachedKey) && !string.IsNullOrEmpty(cachedKey))
                return cachedKey;

            return await FetchAndCacheKeyAsync(keyName, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<string?> RefreshApiKeyAsync(string keyName, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _cachedApiKeys.Remove(keyName);
            return await FetchAndCacheKeyAsync(keyName, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<string?> FetchAndCacheKeyAsync(string keyName, CancellationToken cancellationToken)
    {
        var serverUrl = _configuration["NagiApiServer:Url"];
        var serverKey = _configuration["NagiApiServer:ApiKey"];
        var subscriptionKey = _configuration["NagiApiServer:SubscriptionKey"];

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(serverKey))
        {
            Debug.WriteLine("CRITICAL: Nagi API Server URL or ApiKey is not configured.");
            return null;
        }

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
                Debug.WriteLine(
                    $"Error fetching API key '{keyName}'. Status: {response.StatusCode}, Endpoint: {endpoint}");
                _cachedApiKeys[keyName] = null;
                return null;
            }

            var key = await response.Content.ReadAsStringAsync(cancellationToken);
            var finalKey = key.Trim().Trim('"');

            _cachedApiKeys[keyName] = finalKey;
            return finalKey;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Exception while fetching API key '{keyName}': {ex.Message}");
            _cachedApiKeys[keyName] = null;
            return null;
        }
    }
}