using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Handles direct API communication with Last.fm for scrobbling and "now playing" updates.
/// </summary>
public class LastFmScrobblerService : ILastFmScrobblerService
{
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private const string ApiKeyName = "lastfm";
    private const string ApiSecretName = "lastfm-secret";

    private readonly IApiKeyService _apiKeyService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LastFmScrobblerService> _logger;
    private readonly ISettingsService _settingsService;

    public LastFmScrobblerService(
        IHttpClientFactory httpClientFactory,
        IApiKeyService apiKeyService,
        ISettingsService settingsService,
        ILogger<LastFmScrobblerService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("LastFm");
        _apiKeyService = apiKeyService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<bool> UpdateNowPlayingAsync(Song song)
    {
        var (apiKey, apiSecret, sessionKey) = await GetApiCredentialsAsync().ConfigureAwait(false);
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

        var success = await PostToLastFmAsync(parameters, apiSecret).ConfigureAwait(false);
        if (success)
            _logger.LogDebug("Successfully updated Last.fm 'Now Playing' for track '{TrackTitle}'.", song.Title);
        return success;
    }

    public async Task<bool> ScrobbleAsync(Song song, DateTime playStartTime)
    {
        var (apiKey, apiSecret, sessionKey) = await GetApiCredentialsAsync().ConfigureAwait(false);
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

        return await PostToLastFmAsync(parameters, apiSecret).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends a POST request to the Last.fm API with the provided parameters.
    /// </summary>
    private async Task<bool> PostToLastFmAsync(Dictionary<string, string> parameters, string apiSecret)
    {
        parameters["api_sig"] = CreateSignature(parameters, apiSecret);

        using var formContent = new FormUrlEncodedContent(parameters);

        try
        {
            using var response = await _httpClient.PostAsync(LastFmApiBaseUrl, formContent).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return true;

            var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _logger.LogWarning(
                "Last.fm API call for method '{Method}' failed. Status: {StatusCode}, Response: {ResponseContent}",
                parameters["method"], response.StatusCode, errorContent);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An exception occurred during Last.fm API call for method '{Method}'.",
                parameters["method"]);
            return false;
        }
    }

    /// <summary>
    ///     Retrieves all necessary API keys and session tokens for an authenticated request.
    /// </summary>
    private async Task<(string? ApiKey, string? ApiSecret, string? SessionKey)> GetApiCredentialsAsync()
    {
        var apiKeyTask = _apiKeyService.GetApiKeyAsync(ApiKeyName);
        var apiSecretTask = _apiKeyService.GetApiKeyAsync(ApiSecretName);
        var credentialsTask = _settingsService.GetLastFmCredentialsAsync();

        await Task.WhenAll(apiKeyTask, apiSecretTask, credentialsTask).ConfigureAwait(false);

        var apiKey = apiKeyTask.Result;
        var apiSecret = apiSecretTask.Result;
        var credentials = credentialsTask.Result;

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret) ||
            string.IsNullOrEmpty(credentials?.SessionKey))
        {
            _logger.LogError("Cannot perform Last.fm action; API key, secret, or session key is unavailable.");
            return (null, null, null);
        }

        return (apiKey, apiSecret, credentials.Value.SessionKey);
    }

    /// <summary>
    ///     Creates the required MD5 signature for an authenticated Last.fm API call.
    /// </summary>
    private static string CreateSignature(IDictionary<string, string> parameters, string secret)
    {
        return Helpers.LastFmApiHelper.CreateSignature(parameters, secret);
    }
}