using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Http;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Handles direct API communication with Last.fm for scrobbling and "now playing" updates.
///     Also implements <see cref="IListenSubmitter" /> so <see cref="IOfflineScrobbleService" />
///     can fan out queue processing to this destination alongside other scrobblers.
/// </summary>
public class LastFmScrobblerService : ILastFmScrobblerService, IListenSubmitter
{
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private const string ApiKeyName = ServiceProviderIds.LastFm;
    private const string ApiSecretName = ServiceProviderIds.LastFmSecret;
    private const int MaxRetries = 3;

    private readonly IApiKeyService _apiKeyService;
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LastFmScrobblerService> _logger;
    private readonly ISettingsService _settingsService;

    public LastFmScrobblerService(
        IHttpClientFactory httpClientFactory,
        IApiKeyService apiKeyService,
        ISettingsService settingsService,
        IDbContextFactory<MusicDbContext> contextFactory,
        ILogger<LastFmScrobblerService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
        _settingsService = settingsService;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    // Ordered so default(SubmissionOutcome) == TransientFailure (HttpRetryHelper returns
    // default(T) on exhausted retries or a caught transient exception).
    private enum SubmissionOutcome
    {
        TransientFailure = 0,
        Success,
        PermanentFailure
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

        var success = await PostToLastFmAsync(parameters, apiSecret, CancellationToken.None).ConfigureAwait(false)
            == SubmissionOutcome.Success;
        if (success)
            _logger.LogDebug("Successfully updated Last.fm 'Now Playing' for track '{TrackTitle}'.", song.Title);
        return success;
    }

    public async Task<bool> ScrobbleAsync(Song song, DateTime playStartTime) =>
        await ScrobbleCoreAsync(song, playStartTime).ConfigureAwait(false) == SubmissionOutcome.Success;

    private async Task<SubmissionOutcome> ScrobbleCoreAsync(Song song, DateTime playStartTime)
    {
        var (apiKey, apiSecret, sessionKey) = await GetApiCredentialsAsync().ConfigureAwait(false);
        if (apiKey is null || apiSecret is null || sessionKey is null) return SubmissionOutcome.TransientFailure;

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

    /// <inheritdoc />
    public string Id => "lastfm";

    /// <inheritdoc />
    public Task<bool> IsEnabledAsync() => _settingsService.GetLastFmScrobblingEnabledAsync();

    /// <inheritdoc />
    public async Task ProcessPendingListensAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        if (!await IsEnabledAsync().ConfigureAwait(false)) return;

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var pending = await ctx.ListenHistory
            .AsTracking()
            .Where(lh => lh.IsEligibleForScrobbling && !lh.IsScrobbled)
            .Include(lh => lh.Song).ThenInclude(s => s!.Album)
            .OrderBy(lh => lh.ListenTimestampUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (pending.Count == 0) return;

        var changedCount = 0;
        foreach (var entry in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (entry.Song is null) continue;

            SubmissionOutcome outcome;
            try
            {
                outcome = await ScrobbleCoreAsync(entry.Song, entry.ListenTimestampUtc).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Last.fm queue submission raised. Will retry later. Song '{Title}'.", entry.Song.Title);
                break;
            }

            if (outcome == SubmissionOutcome.TransientFailure)
            {
                _logger.LogDebug("Last.fm transient failure for '{Title}'. Stopping to preserve order.",
                    entry.Song.Title);
                break;
            }

            if (outcome == SubmissionOutcome.PermanentFailure)
                _logger.LogWarning("Last.fm permanently rejected '{Title}'; dropping from queue.",
                    entry.Song.Title);

            entry.IsScrobbled = true;
            changedCount++;
        }

        if (changedCount > 0)
            await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends a POST request to the Last.fm API with the provided parameters.
    /// </summary>
    private async Task<SubmissionOutcome> PostToLastFmAsync(
        Dictionary<string, string> parameters,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        parameters["api_sig"] = CreateSignature(parameters, apiSecret);
        var method = parameters["method"];
        var operationName = $"Last.fm {method}";

        return await HttpRetryHelper.ExecuteWithRetryAsync<SubmissionOutcome>(
            async attempt =>
            {
                using var formContent = new FormUrlEncodedContent(parameters);
                _logger.LogDebug("Calling Last.fm method '{Method}' (Attempt {Attempt}/{MaxRetries})",
                    method, attempt, MaxRetries);

                using var response = await _httpClient.PostAsync(LastFmApiBaseUrl, formContent, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return RetryResult<SubmissionOutcome>.Success(SubmissionOutcome.Success);

                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Last.fm API call for method '{Method}' failed. Status: {StatusCode}, Response: {ResponseContent}. Attempt {Attempt}/{MaxRetries}",
                    method, response.StatusCode, errorContent, attempt, MaxRetries);

                if (HttpRetryHelper.IsRetryableStatusCode(response.StatusCode))
                    return RetryResult<SubmissionOutcome>.TransientFailure();

                return RetryResult<SubmissionOutcome>.Success(SubmissionOutcome.PermanentFailure);
            },
            _logger,
            operationName,
            cancellationToken
        ).ConfigureAwait(false);
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
