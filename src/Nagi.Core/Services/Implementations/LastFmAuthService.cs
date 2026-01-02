using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Handles the Last.fm authentication flow by making calls to the Last.fm API.
/// </summary>
public class LastFmAuthService : ILastFmAuthService
{
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private const string ApiKeyName = "lastfm";
    private const string ApiSecretName = "lastfm-secret";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IApiKeyService _apiKeyService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LastFmAuthService> _logger;

    public LastFmAuthService(IHttpClientFactory httpClientFactory, IApiKeyService apiKeyService,
        ILogger<LastFmAuthService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(string Token, string AuthUrl)?> GetAuthenticationTokenAsync()
    {
        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName);
        var apiSecret = await _apiKeyService.GetApiKeyAsync(ApiSecretName);

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            _logger.LogError("Cannot get Last.fm auth token; API key or secret is unavailable.");
            return null;
        }

        var parameters = new Dictionary<string, string>
        {
            { "method", "auth.getToken" },
            { "api_key", apiKey }
        };

        var signature = CreateSignature(parameters, apiSecret);
        var requestUrl = $"{LastFmApiBaseUrl}?method=auth.getToken&api_key={apiKey}&api_sig={signature}&format=json";

        try
        {
            using var response = await _httpClient.GetAsync(requestUrl);
            var content = await response.Content.ReadAsStringAsync();

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An exception occurred while getting the Last.fm authentication token.");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<(string Username, string SessionKey)?> GetSessionAsync(string token)
    {
        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName);
        var apiSecret = await _apiKeyService.GetApiKeyAsync(ApiSecretName);

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            _logger.LogError("Cannot get Last.fm session; API key or secret is unavailable.");
            return null;
        }

        var parameters = new Dictionary<string, string>
        {
            { "method", "auth.getSession" },
            { "api_key", apiKey },
            { "token", token }
        };

        var signature = CreateSignature(parameters, apiSecret);
        var requestUrl =
            $"{LastFmApiBaseUrl}?method=auth.getSession&api_key={apiKey}&token={token}&api_sig={signature}&format=json";

        try
        {
            using var response = await _httpClient.GetAsync(requestUrl);
            var content = await response.Content.ReadAsStringAsync();

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An exception occurred while getting the Last.fm session.");
            return null;
        }
    }

    private static string CreateSignature(IDictionary<string, string> parameters, string secret)
    {
        return Helpers.LastFmApiHelper.CreateSignature(parameters, secret);
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