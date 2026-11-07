using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Globalization;
using System.Xml.Linq;
using Nagi.Core.Data;
using Nagi.Core.Library;
using Nagi.Core.OnlineProviders;
using Nagi.Core.OnlineProviders.Pipelines;
using Nagi.Core.Settings;

namespace Nagi.Core.Scrobbling;

/// <summary>
///     Sends listens and "now playing" to Last.fm, for an account signed in with scrobbling or "now playing" turned on.
///     Every eligible listen counts, however long before scrobbling was turned on.
/// </summary>
public class LastFmScrobblerService : IListenSubmitter
{
    private readonly IApiKeyService _apiKeyService;
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly HttpClient _httpClient;
    private readonly ProviderPipelineProvider _pipelines;
    private readonly ILogger<LastFmScrobblerService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _submitLock = new(1, 1);

    public LastFmScrobblerService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
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

    public string Name => "Last.fm";

    public async Task UpdateNowPlayingAsync(Song song)
    {
        if (!_settingsService.Get(CoreSettings.LastFmNowPlaying)) return;
        try
        {
            if (await GetApiCredentialsAsync(CancellationToken.None).ConfigureAwait(false) is not { } credentials) return;

            var parameters = new Dictionary<string, string>
            {
                { "method", "track.updateNowPlaying" },
                { "artist", song.PrimaryArtistName },
                { "track", song.Title },
                { "api_key", credentials.ApiKey },
                { "sk", credentials.SessionKey }
            };
            AddTrackDetails(parameters, song);

            if (await PostToLastFmAsync(parameters, credentials.ApiSecret, CancellationToken.None).ConfigureAwait(false)
                == SubmissionOutcome.Success)
                _logger.LogDebug("Updated Last.fm 'Now Playing' for track '{TrackTitle}'.", song.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update Last.fm 'Now Playing' for track {TrackTitle}.", song.Title);
        }
    }

    public async Task SubmitPendingAsync(CancellationToken cancellationToken)
    {
        if (!_settingsService.Get(CoreSettings.LastFmScrobbling)) return;
        await _submitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await GetApiCredentialsAsync(cancellationToken).ConfigureAwait(false) is not { } credentials) return;
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

                var outcome = await ScrobbleAsync(entry.Song, entry.ListenTimestampUtc, credentials, cancellationToken)
                    .ConfigureAwait(false);
                if (outcome == SubmissionOutcome.TransientFailure)
                {
                    _logger.LogDebug("Last.fm transient failure for '{Title}'. Stopping to preserve order.", entry.Song.Title);
                    break;
                }

                if (outcome == SubmissionOutcome.PermanentFailure)
                    _logger.LogWarning("Last.fm permanently rejected '{Title}'; dropping from queue.", entry.Song.Title);

                entry.IsScrobbled = true;
                // Saved even if shutdown cancels the rest, so a listen Last.fm already has isn't sent again.
                await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _submitLock.Release();
        }
    }

    private Task<SubmissionOutcome> ScrobbleAsync(Song song, DateTime playStartUtc, LastFmCredentials credentials,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            { "method", "track.scrobble" },
            { "artist", song.PrimaryArtistName },
            { "track", song.Title },
            { "timestamp", new DateTimeOffset(playStartUtc).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) },
            { "api_key", credentials.ApiKey },
            { "sk", credentials.SessionKey }
        };
        AddTrackDetails(parameters, song);
        return PostToLastFmAsync(parameters, credentials.ApiSecret, cancellationToken);
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

    // Rate limit, retry and circuit breaker are handled by the provider pipeline; this only classifies the answer.
    private async Task<SubmissionOutcome> PostToLastFmAsync(
        Dictionary<string, string> parameters,
        string apiSecret,
        CancellationToken cancellationToken)
    {
        parameters["api_sig"] = LastFmApiHelper.CreateSignature(parameters, apiSecret);
        var method = parameters["method"];

        return await _pipelines.ExecuteWithFallbackAsync(
            ProviderCatalog.LastFm.Policy.ProviderId,
            async ct =>
            {
                using var formContent = new FormUrlEncodedContent(parameters);
                _logger.LogDebug("Calling Last.fm method '{Method}'.", method);
                return await _httpClient.PostAsync(LastFmApiHelper.BaseUrl, formContent, ct).ConfigureAwait(false);
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

    /// <returns>The keys and the session, or null when Last.fm isn't signed in to or the keys can't be fetched.</returns>
    private async Task<LastFmCredentials?> GetApiCredentialsAsync(CancellationToken cancellationToken)
    {
        var keysTask = LastFmApiHelper.GetKeyAndSecretAsync(_apiKeyService, cancellationToken);
        var credentialsTask = _settingsService.GetLastFmCredentialsAsync();

        var keys = await keysTask.ConfigureAwait(false);
        var credentials = await credentialsTask.ConfigureAwait(false);

        if (keys is null || string.IsNullOrEmpty(credentials?.SessionKey))
        {
            _logger.LogError("Cannot perform Last.fm action; API key, secret, or session key is unavailable.");
            return null;
        }

        return new LastFmCredentials(keys.Value.ApiKey, keys.Value.ApiSecret, credentials.Value.SessionKey);
    }

    private sealed record LastFmCredentials(string ApiKey, string ApiSecret, string SessionKey);
}
