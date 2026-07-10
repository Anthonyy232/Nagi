using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Handles the Last.fm authentication flow by making calls to the Last.fm API.
/// </summary>
public class LastFmAuthService : ILastFmAuthService
{
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

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

    /// <inheritdoc />
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

        var signature = CreateSignature(parameters, apiSecret);
        var requestUrl = $"{LastFmApiBaseUrl}?method=auth.getToken&api_key={apiKey}&api_sig={signature}&format=json";

        return await _pipelines.ExecuteWithFallbackAsync<(string Token, string AuthUrl)?>(
            ServiceProviderIds.LastFm,
            async ct =>
            {
                _logger.LogDebug("Getting Last.fm auth token.");
                return await _httpClient.GetAsync(requestUrl, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to get Last.fm auth token. Status: {StatusCode}, Response: {ResponseContent}",
                        response.StatusCode, content);
                    return null;
                }

                var tokenResponse = JsonSerializer.Deserialize<LastFmTokenResponse>(content, _jsonOptions);
                if (string.IsNullOrEmpty(tokenResponse?.Token))
                {
                    _logger.LogError("Failed to extract token from the Last.fm auth response.");
                    return null;
                }

                var authUrl = $"https://www.last.fm/api/auth/?api_key={apiKey}&token={tokenResponse.Token}";
                return (tokenResponse.Token, authUrl);
            },
            fallback: null,
            _logger,
            "auth token fetch").ConfigureAwait(false);
    }

    /// <inheritdoc />
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

        var signature = CreateSignature(parameters, apiSecret);
        var requestUrl =
            $"{LastFmApiBaseUrl}?method=auth.getSession&api_key={apiKey}&token={token}&api_sig={signature}&format=json";

        return await _pipelines.ExecuteWithFallbackAsync<(string Username, string SessionKey)?>(
            ServiceProviderIds.LastFm,
            async ct =>
            {
                _logger.LogDebug("Getting Last.fm session.");
                return await _httpClient.GetAsync(requestUrl, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to get Last.fm session. Status: {StatusCode}, Response: {ResponseContent}",
                        response.StatusCode, content);
                    return null;
                }

                var sessionResponse = JsonSerializer.Deserialize<LastFmSessionResponse>(content, _jsonOptions);
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

    private static string CreateSignature(IDictionary<string, string> parameters, string secret)
    {
        return Helpers.LastFmApiHelper.CreateSignature(parameters, secret);
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

    // Helper classes for deserializing Last.fm API responses.
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
