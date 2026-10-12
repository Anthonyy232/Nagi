using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

public class LastFmAuthService : ILastFmAuthService
{
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";

    private readonly IApiKeyService _apiKeyService;
    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<LastFmAuthService> _logger;

    public LastFmAuthService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ILogger<LastFmAuthService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    public async Task<(string Token, string AuthUrl)?> GetAuthenticationTokenAsync()
    {
        var credentials = await GetCredentialsAsync().ConfigureAwait(false);
        if (credentials is null)
        {
            _logger.LogError("Cannot get Last.fm auth token; API key or secret is unavailable.");
            return null;
        }
        var (apiKey, apiSecret) = credentials.Value;

        var parameters = new Dictionary<string, string>
        {
            { "method", "auth.getToken" },
            { "api_key", apiKey }
        };

        var signature = Helpers.LastFmApiHelper.CreateSignature(parameters, apiSecret);
        var requestUrl = $"{LastFmApiBaseUrl}?method=auth.getToken&api_key={Uri.EscapeDataString(apiKey)}&api_sig={signature}&format=json";

        return await _pipelines.ExecuteWithFallbackAsync<(string Token, string AuthUrl)?>(
            ServiceProviderIds.LastFm,
            ct => new ValueTask<HttpResponseMessage>(_httpClient.GetAsync(requestUrl, ct)),
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogError("Failed to get Last.fm auth token. Status: {StatusCode}, Response: {ResponseContent}",
                        response.StatusCode, content);
                    return null;
                }

                var tokenResponse = await response.Content.ReadFromJsonAsync<LastFmTokenResponse>(cancellationToken: ct)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(tokenResponse?.Token))
                {
                    _logger.LogError("Failed to extract token from the Last.fm auth response.");
                    return null;
                }

                var authUrl = $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(apiKey)}&token={Uri.EscapeDataString(tokenResponse.Token)}";
                return (tokenResponse.Token, authUrl);
            },
            fallback: null,
            _logger,
            "auth token fetch").ConfigureAwait(false);
    }

    public async Task<(string Username, string SessionKey)?> GetSessionAsync(string token)
    {
        var credentials = await GetCredentialsAsync().ConfigureAwait(false);
        if (credentials is null)
        {
            _logger.LogError("Cannot get Last.fm session; API key or secret is unavailable.");
            return null;
        }
        var (apiKey, apiSecret) = credentials.Value;

        var parameters = new Dictionary<string, string>
        {
            { "method", "auth.getSession" },
            { "api_key", apiKey },
            { "token", token }
        };

        var signature = Helpers.LastFmApiHelper.CreateSignature(parameters, apiSecret);
        var requestUrl =
            $"{LastFmApiBaseUrl}?method=auth.getSession&api_key={Uri.EscapeDataString(apiKey)}&token={Uri.EscapeDataString(token)}&api_sig={signature}&format=json";

        return await _pipelines.ExecuteWithFallbackAsync<(string Username, string SessionKey)?>(
            ServiceProviderIds.LastFm,
            ct => new ValueTask<HttpResponseMessage>(_httpClient.GetAsync(requestUrl, ct)),
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogError("Failed to get Last.fm session. Status: {StatusCode}, Response: {ResponseContent}",
                        response.StatusCode, content);
                    return null;
                }

                var sessionResponse = await response.Content.ReadFromJsonAsync<LastFmSessionResponse>(cancellationToken: ct)
                    .ConfigureAwait(false);
                var session = sessionResponse?.Session;

                if (session != null && !string.IsNullOrEmpty(session.Key) && !string.IsNullOrEmpty(session.Name))
                {
                    _logger.LogInformation("Successfully retrieved Last.fm session for user {Username}.", session.Name);
                    return (session.Name, session.Key);
                }

                _logger.LogError("Failed to deserialize or extract session details from the Last.fm response.");
                return null;
            },
            fallback: null,
            _logger,
            "session fetch").ConfigureAwait(false);
    }

    private async Task<(string ApiKey, string ApiSecret)?> GetCredentialsAsync()
    {
        var apiKeyTask = _apiKeyService.GetApiKeyAsync(ServiceProviderIds.LastFm);
        var apiSecretTask = _apiKeyService.GetApiKeyAsync(ServiceProviderIds.LastFmSecret);

        await Task.WhenAll(apiKeyTask, apiSecretTask).ConfigureAwait(false);
        var apiKey = await apiKeyTask.ConfigureAwait(false);
        var apiSecret = await apiSecretTask.ConfigureAwait(false);

        return string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret)
            ? null
            : (apiKey, apiSecret);
    }

    private class LastFmTokenResponse
    {
        public string? Token { get; set; }
    }

    private class LastFmSessionResponse
    {
        public LastFmSession? Session { get; set; }
    }

    private class LastFmSession
    {
        public string? Name { get; set; }
        public string? Key { get; set; }
    }
}
