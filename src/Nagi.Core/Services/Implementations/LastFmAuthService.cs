using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    public LastFmAuthService(IHttpClientFactory httpClientFactory, IApiKeyService apiKeyService)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
    }

    /// <inheritdoc />
    public async Task<(string Token, string AuthUrl)?> GetAuthenticationTokenAsync()
    {
        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName);
        var apiSecret = await _apiKeyService.GetApiKeyAsync(ApiSecretName);

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            Debug.WriteLine("[ERROR] LastFmAuthService: Cannot get auth token; API key or secret is unavailable.");
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
                Debug.WriteLine(
                    $"[ERROR] LastFmAuthService: Failed to get token. Status: {response.StatusCode}, Response: {content}");
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<LastFmTokenResponse>(content, _jsonOptions);
            if (string.IsNullOrEmpty(tokenResponse?.Token))
            {
                Debug.WriteLine("[ERROR] LastFmAuthService: Failed to extract token from the response.");
                return null;
            }

            var authUrl = $"https://www.last.fm/api/auth/?api_key={apiKey}&token={tokenResponse.Token}";
            return (tokenResponse.Token, authUrl);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] LastFmAuthService: Exception during GetAuthenticationTokenAsync: {ex.Message}");
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
            Debug.WriteLine("[ERROR] LastFmAuthService: Cannot get session; API key or secret is unavailable.");
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
                Debug.WriteLine(
                    $"[ERROR] LastFmAuthService: Failed to get session. Status: {response.StatusCode}, Response: {content}");
                return null;
            }

            var sessionResponse = JsonSerializer.Deserialize<LastFmSessionResponse>(content, _jsonOptions);
            var session = sessionResponse?.Session;

            if (session != null && !string.IsNullOrEmpty(session.Key) && !string.IsNullOrEmpty(session.Name))
                return (session.Name, session.Key);

            Debug.WriteLine(
                "[ERROR] LastFmAuthService: Failed to deserialize or extract session details from the response.");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] LastFmAuthService: Exception during GetSessionAsync: {ex.Message}");
            return null;
        }
    }

    private static string CreateSignature(IDictionary<string, string> parameters, string secret)
    {
        var sb = new StringBuilder();

        // Parameters must be ordered alphabetically by key.
        foreach (var kvp in parameters.OrderBy(p => p.Key))
        {
            sb.Append(kvp.Key);
            sb.Append(kvp.Value);
        }

        sb.Append(secret);

        using var md5 = MD5.Create();
        var inputBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hashBytes = md5.ComputeHash(inputBytes);

        var hashStringBuilder = new StringBuilder();
        foreach (var t in hashBytes) hashStringBuilder.Append(t.ToString("x2"));

        return hashStringBuilder.ToString();
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