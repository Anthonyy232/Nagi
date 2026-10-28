using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Globalization;
using System.Xml.Linq;
using Nagi.Core.Data;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Handles direct API communication with Last.fm for scrobbling and "now playing" updates.
///     Also implements <see cref="IListenSubmitter" /> so <see cref="OfflineScrobbleService" />
///     can fan out queue processing to this destination alongside other scrobblers.
/// </summary>
public class LastFmScrobblerService : ILastFmScrobblerService, IListenSubmitter
{
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";

    private readonly IApiKeyService _apiKeyService;
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<LastFmScrobblerService> _logger;
    private readonly ISettingsService _settingsService;

    public LastFmScrobblerService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ISettingsService settingsService,
        IDbContextFactory<MusicDbContext> contextFactory,
        ILogger<LastFmScrobblerService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _apiKeyService = apiKeyService;
        _settingsService = settingsService;
        _contextFactory = contextFactory;
        _logger = logger;
    }

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

        AddTrackDetails(parameters, song);

        var success = await PostToLastFmAsync(parameters, apiSecret, CancellationToken.None).ConfigureAwait(false)
            == SubmissionOutcome.Success;
        if (success)
            _logger.LogDebug("Successfully updated Last.fm 'Now Playing' for track '{TrackTitle}'.", song.Title);
        return success;
    }

    public async Task<bool> ScrobbleAsync(Song song, DateTime playStartTime) =>
        await ScrobbleCoreAsync(song, playStartTime).ConfigureAwait(false) == SubmissionOutcome.Success;

    private async Task<SubmissionOutcome> ScrobbleCoreAsync(Song song, DateTime playStartTime,
        CancellationToken cancellationToken = default)
    {
        var (apiKey, apiSecret, sessionKey) = await GetApiCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (apiKey is null || apiSecret is null || sessionKey is null) return SubmissionOutcome.TransientFailure;

        // SQLite preserves UTC ticks but does not retain DateTime.Kind.
        var utc = playStartTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(playStartTime, DateTimeKind.Utc) : playStartTime.ToUniversalTime();
        var timestamp = new DateTimeOffset(utc).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var parameters = new Dictionary<string, string>
        {
            { "method", "track.scrobble" },
            { "artist", song.PrimaryArtistName },
            { "track", song.Title },
            { "timestamp", timestamp },
            { "api_key", apiKey },
            { "sk", sessionKey }
        };

        AddTrackDetails(parameters, song);

        return await PostToLastFmAsync(parameters, apiSecret, cancellationToken).ConfigureAwait(false);
    }

    private static void AddTrackDetails(Dictionary<string, string> parameters, Song song)
    {
        if (song.Album != null)
        {
            parameters.Add("album", song.Album.Title);
            if (!string.Equals(song.Album.ArtistName, song.PrimaryArtistName, StringComparison.OrdinalIgnoreCase))
                parameters.Add("albumArtist", song.Album.ArtistName);
        }

        if (song.Duration > TimeSpan.Zero)
            parameters.Add("duration", ((long)song.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        if (song.TrackNumber is > 0)
            parameters.Add("trackNumber", song.TrackNumber.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(song.MusicBrainzTrackId))
            parameters.Add("mbid", song.MusicBrainzTrackId);
    }

    public string Id => ServiceProviderIds.LastFm;

    public Task<bool> IsEnabledAsync() => _settingsService.GetLastFmScrobblingEnabledAsync();

    public async Task ProcessPendingListensAsync(CancellationToken cancellationToken)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var pending = await ctx.ListenHistory
            .AsTracking()
            .Where(lh => lh.IsEligibleForScrobbling && !lh.IsScrobbled)
            .Include(lh => lh.Song).ThenInclude(s => s.Album)
            .OrderBy(lh => lh.ListenTimestampUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;

            SubmissionOutcome outcome;
            try
            {
                outcome = await ScrobbleCoreAsync(entry.Song, entry.ListenTimestampUtc, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
            // Persist the acknowledgement even if shutdown cancels further submissions.
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Sends a POST request to the Last.fm API with the provided parameters.
    ///     Rate limit / retry / circuit breaker are handled by the provider pipeline; this
    ///     method only classifies the final response.
    /// </summary>
    private async Task<SubmissionOutcome> PostToLastFmAsync(
        Dictionary<string, string> parameters,
        string apiSecret,
        CancellationToken cancellationToken = default)
    {
        parameters["api_sig"] = Helpers.LastFmApiHelper.CreateSignature(parameters, apiSecret);
        var method = parameters["method"];

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.LastFm,
            async ct =>
            {
                using var formContent = new FormUrlEncodedContent(parameters);
                _logger.LogDebug("Calling Last.fm method '{Method}'.", method);
                return await _httpClient.PostAsync(LastFmApiBaseUrl, formContent, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var document = XElement.Parse(content);
                var status = (string?)document.Attribute("status");
                if (response.IsSuccessStatusCode && document.Name == "lfm" && status == "ok")
                {
                    var result = method == "track.scrobble"
                        ? document.Element("scrobbles")?.Element("scrobble")
                        : document.Element("nowplaying");
                    // An "ok" envelope can still reject a listen. Future timestamps and
                    // daily limits can recover; missing/unknown acknowledgements stay queued.
                    return (int?)result?.Element("ignoredMessage")?.Attribute("code") switch
                    {
                        0 => SubmissionOutcome.Success,
                        1 or 2 or 3 => SubmissionOutcome.PermanentFailure,
                        _ => SubmissionOutcome.TransientFailure
                    };
                }

                _logger.LogWarning(
                    "Last.fm API call for method '{Method}' failed. Status: {StatusCode}, Response: {ResponseContent}.",
                    method, response.StatusCode, content);

                // Authentication can be repaired without changing the listen; keep it queued.
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    || HttpStatusClassification.IsTransient(response.StatusCode))
                    return SubmissionOutcome.TransientFailure;

                if (document.Name != "lfm" || status != "failed")
                    return SubmissionOutcome.TransientFailure;

                return (int?)document.Element("error")?.Attribute("code") switch
                {
                    6 or 7 => SubmissionOutcome.PermanentFailure,
                    _ => SubmissionOutcome.TransientFailure
                };
            },
            fallback: SubmissionOutcome.TransientFailure,
            _logger,
            $"call to '{method}'",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string? ApiKey, string? ApiSecret, string? SessionKey)> GetApiCredentialsAsync(
        CancellationToken cancellationToken = default)
    {
        var apiKeyTask = _apiKeyService.GetApiKeyAsync(ServiceProviderIds.LastFm, cancellationToken);
        var apiSecretTask = _apiKeyService.GetApiKeyAsync(ServiceProviderIds.LastFmSecret, cancellationToken);
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
}
