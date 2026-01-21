using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Handles direct API communication with Last.fm for scrobbling and "now playing" updates.
/// </summary>
public class LastFmScrobblerService : ILastFmScrobblerService
{
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private const string ApiKeyName = ServiceProviderIds.LastFm;
    private const string ApiSecretName = ServiceProviderIds.LastFmSecret;
    private const int MaxRetries = 3;

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
        _httpClient = httpClientFactory.CreateClient();
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
            { "artist", song.PrimaryArtistName },
            { "track", song.Title },
            { "api_key", apiKey },
            { "sk", sessionKey }
        };

        if (song.Album != null) parameters.Add("album", song.Album.Title);
        if (song.Duration > TimeSpan.Zero) parameters.Add("duration", ((int)song.Duration.TotalSeconds).ToString());

        var success = await PostToLastFmAsync(parameters, apiSecret, CancellationToken.None).ConfigureAwait(false);
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
            { "artist", song.PrimaryArtistName },
            { "track", song.Title },
            { "timestamp", timestamp },
            { "api_key", apiKey },
            { "sk", sessionKey }
        };

        if (song.Album != null) parameters.Add("album", song.Album.Title);

        return await PostToLastFmAsync(parameters, apiSecret, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends a POST request to the Last.fm API with the provided parameters.
    /// </summary>
    private async Task<bool> PostToLastFmAsync(
        Dictionary<string, string> parameters, 
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        parameters["api_sig"] = CreateSignature(parameters, apiSecret);
        var method = parameters["method"];
        var operationName = $"Last.fm {method}";

        var result = await HttpRetryHelper.ExecuteWithRetryAsync<bool>(
            async attempt =>
            {
                using var formContent = new FormUrlEncodedContent(parameters);
                _logger.LogDebug("Calling Last.fm method '{Method}' (Attempt {Attempt}/{MaxRetries})", 
                    method, attempt, MaxRetries);

                using var response = await _httpClient.PostAsync(LastFmApiBaseUrl, formContent, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) 
                    return RetryResult<bool>.Success(true);

                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Last.fm API call for method '{Method}' failed. Status: {StatusCode}, Response: {ResponseContent}. Attempt {Attempt}/{MaxRetries}",
                    method, response.StatusCode, errorContent, attempt, MaxRetries);
                
                if (HttpRetryHelper.IsRetryableStatusCode(response.StatusCode))
                    return RetryResult<bool>.TransientFailure();
                
                return RetryResult<bool>.Success(false);
            },
            _logger,
            operationName,
            cancellationToken
        ).ConfigureAwait(false);

        return result;
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

        var apiKey = await apiKeyTask.ConfigureAwait(false);
        var apiSecret = await apiSecretTask.ConfigureAwait(false);
        var credentials = await credentialsTask.ConfigureAwait(false);

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