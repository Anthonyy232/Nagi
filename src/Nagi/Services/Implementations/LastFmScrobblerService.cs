using Nagi.Models;
using Nagi.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Nagi.Services.Implementations;

/// <summary>
/// Handles direct API communication with Last.fm for scrobbling and "now playing" updates.
/// </summary>
public class LastFmScrobblerService : ILastFmScrobblerService {
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private const string ApiKeyName = "lastfm";
    private const string ApiSecretName = "lastfm-secret";

    private readonly IApiKeyService _apiKeyService;
    private readonly ISettingsService _settingsService;
    private readonly HttpClient _httpClient;

    public LastFmScrobblerService(
        IHttpClientFactory httpClientFactory,
        IApiKeyService apiKeyService,
        ISettingsService settingsService) {
        _httpClient = httpClientFactory.CreateClient("LastFm");
        _apiKeyService = apiKeyService;
        _settingsService = settingsService;
    }

    public async Task<bool> UpdateNowPlayingAsync(Song song) {
        var (apiKey, apiSecret, sessionKey) = await GetApiCredentialsAsync();
        if (apiKey is null || apiSecret is null || sessionKey is null) return false;

        var parameters = new Dictionary<string, string>
        {
            { "method", "track.updateNowPlaying" },
            { "artist", song.Artist?.Name ?? "Unknown Artist" },
            { "track", song.Title },
            { "api_key", apiKey },
            { "sk", sessionKey }
        };

        if (song.Album != null) parameters.Add("album", song.Album.Title);
        if (song.Duration > TimeSpan.Zero) parameters.Add("duration", ((int)song.Duration.TotalSeconds).ToString());

        return await PostToLastFmAsync(parameters, apiSecret);
    }

    public async Task<bool> ScrobbleAsync(Song song, DateTime playStartTime) {
        var (apiKey, apiSecret, sessionKey) = await GetApiCredentialsAsync();
        if (apiKey is null || apiSecret is null || sessionKey is null) return false;

        var timestamp = new DateTimeOffset(playStartTime).ToUnixTimeSeconds().ToString();

        var parameters = new Dictionary<string, string>
        {
            { "method", "track.scrobble" },
            { "artist", song.Artist?.Name ?? "Unknown Artist" },
            { "track", song.Title },
            { "timestamp", timestamp },
            { "api_key", apiKey },
            { "sk", sessionKey }
        };

        if (song.Album != null) parameters.Add("album", song.Album.Title);

        return await PostToLastFmAsync(parameters, apiSecret);
    }

    /// <summary>
    /// Sends a POST request to the Last.fm API with the provided parameters.
    /// </summary>
    private async Task<bool> PostToLastFmAsync(Dictionary<string, string> parameters, string apiSecret) {
        parameters["api_sig"] = CreateSignature(parameters, apiSecret);

        var formContent = new FormUrlEncodedContent(parameters);

        try {
            var response = await _httpClient.PostAsync(LastFmApiBaseUrl, formContent);
            if (response.IsSuccessStatusCode) {
                return true;
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            Debug.WriteLine($"[LastFmScrobblerService] API call failed. Status: {response.StatusCode}, Response: {errorContent}");
            return false;
        }
        catch (Exception ex) {
            Debug.WriteLine($"[LastFmScrobblerService] Exception during API call: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Retrieves all necessary API keys and session tokens for an authenticated request.
    /// </summary>
    private async Task<(string? ApiKey, string? ApiSecret, string? SessionKey)> GetApiCredentialsAsync() {
        var apiKeyTask = _apiKeyService.GetApiKeyAsync(ApiKeyName);
        var apiSecretTask = _apiKeyService.GetApiKeyAsync(ApiSecretName);
        var credentialsTask = _settingsService.GetLastFmCredentialsAsync();

        await Task.WhenAll(apiKeyTask, apiSecretTask, credentialsTask);

        var apiKey = apiKeyTask.Result;
        var apiSecret = apiSecretTask.Result;
        var credentials = credentialsTask.Result;

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret) || string.IsNullOrEmpty(credentials?.SessionKey)) {
            Debug.WriteLine("[LastFmScrobblerService] Cannot perform action; API key, secret, or session key is unavailable.");
            return (null, null, null);
        }

        return (apiKey, apiSecret, credentials.Value.SessionKey);
    }

    /// <summary>
    /// Creates the required MD5 signature for an authenticated Last.fm API call.
    /// </summary>
    private static string CreateSignature(IDictionary<string, string> parameters, string secret) {
        var sb = new StringBuilder();
        // Parameters must be ordered alphabetically by key for a valid signature.
        foreach (var kvp in parameters.OrderBy(p => p.Key)) {
            sb.Append(kvp.Key);
            sb.Append(kvp.Value);
        }
        sb.Append(secret);

        using var md5 = MD5.Create();
        byte[] inputBytes = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] hashBytes = md5.ComputeHash(inputBytes);

        var hashStringBuilder = new StringBuilder();
        foreach (var t in hashBytes) {
            hashStringBuilder.Append(t.ToString("x2"));
        }
        return hashStringBuilder.ToString();
    }
}