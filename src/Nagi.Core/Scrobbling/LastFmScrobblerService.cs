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
    private readonly LastFmSignedClient _client;
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly ILogger<LastFmScrobblerService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IScrobblingAccounts _accounts;

    public LastFmScrobblerService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ISettingsService settingsService,
        IScrobblingAccounts accounts,
        IDbContextFactory<MusicDbContext> contextFactory,
        ILogger<LastFmScrobblerService> logger)
    {
        _client = new LastFmSignedClient(httpClientFactory.CreateClient(), pipelines, apiKeyService, logger);
        _settingsService = settingsService;
        _accounts = accounts;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public string Name => "Last.fm";

    public IReadOnlyCollection<Setting> Settings { get; } = [CoreSettings.LastFmScrobbling, CoreSettings.LastFmNowPlaying];

    public ScrobblingAccount? Account => ScrobblingAccount.LastFm;

    public async Task UpdateNowPlayingAsync(Song song)
    {
        if (!_settingsService.Get(CoreSettings.LastFmNowPlaying)) return;
        try
        {
            if (await GetSessionKeyAsync().ConfigureAwait(false) is not { } sessionKey) return;

            var parameters = new Dictionary<string, string>
            {
                { "method", "track.updateNowPlaying" },
                { "artist", song.PrimaryArtistName },
                { "track", song.Title },
                { "sk", sessionKey }
            };
            AddTrackDetails(parameters, song);

            if (await SendAsync(parameters, CancellationToken.None).ConfigureAwait(false) == SubmissionOutcome.Success)
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
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = await ctx.ListenHistory
            .AsTracking()
            .Where(lh => lh.IsEligibleForScrobbling && !lh.IsScrobbled)
            .Include(lh => lh.Song).ThenInclude(s => s.Album)
            .OrderBy(lh => lh.ListenTimestampUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // Fetching the keys can take a while offline, so it waits until there is something to send.
        if (pending.Count == 0 || await GetSessionKeyAsync().ConfigureAwait(false) is not { } sessionKey) return;

        foreach (var entry in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var outcome = await ScrobbleAsync(entry.Song, entry.ListenTimestampUtc, sessionKey, cancellationToken)
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

    private Task<SubmissionOutcome> ScrobbleAsync(Song song, DateTime playStartUtc, string sessionKey,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            { "method", "track.scrobble" },
            { "artist", song.PrimaryArtistName },
            { "track", song.Title },
            { "timestamp", new DateTimeOffset(playStartUtc).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) },
            { "sk", sessionKey }
        };
        AddTrackDetails(parameters, song);
        return SendAsync(parameters, cancellationToken);
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
    private Task<SubmissionOutcome> SendAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var method = parameters["method"];
        return _client.SendAsync(HttpMethod.Post, parameters, reply =>
            {
                var document = XElement.Parse(reply.Body);
                var status = (string?)document.Attribute("status");
                if (reply.Response.IsSuccessStatusCode && document.Name == "lfm" && status == "ok")
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
                    method, reply.Response.StatusCode, reply.Body);

                // Authentication can be repaired without changing the listen; keep it queued.
                if (reply.Response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    || HttpStatusClassification.IsTransient(reply.Response.StatusCode))
                    return SubmissionOutcome.TransientFailure;

                if (document.Name != "lfm" || status != "failed")
                    return SubmissionOutcome.TransientFailure;

                return (int?)document.Element("error")?.Attribute("code") switch
                {
                    6 or 7 => SubmissionOutcome.PermanentFailure,
                    _ => SubmissionOutcome.TransientFailure
                };
            },
            fallback: SubmissionOutcome.TransientFailure, cancellationToken);
    }

    /// <returns>The session key, or null when Last.fm isn't signed in to.</returns>
    private async Task<string?> GetSessionKeyAsync()
    {
        var sessionKey = (await _accounts.GetLastFmCredentialsAsync().ConfigureAwait(false))?.SessionKey;
        if (string.IsNullOrEmpty(sessionKey))
        {
            _logger.LogError("Cannot perform Last.fm action; the session key is unavailable.");
            return null;
        }

        return sessionKey;
    }
}
