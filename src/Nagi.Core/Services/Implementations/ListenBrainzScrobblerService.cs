using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Http;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Handles HTTP communication with ListenBrainz for listen submission and token validation.
///     Also implements <see cref="IListenSubmitter" /> so <see cref="IOfflineScrobbleService" />
///     can fan out queue processing to this destination.
/// </summary>
public class ListenBrainzScrobblerService : IListenBrainzScrobblerService, IListenSubmitter
{
    private const string DefaultBaseUrl = "https://api.listenbrainz.org";
    private const string SubmitListensPath = "/1/submit-listens";
    private const string ValidateTokenPath = "/1/validate-token";
    private const int MaxRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ListenBrainzScrobblerService> _logger;
    private readonly ISettingsService _settingsService;

    public ListenBrainzScrobblerService(
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<MusicDbContext> contextFactory,
        ISettingsService settingsService,
        ILogger<ListenBrainzScrobblerService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _contextFactory = contextFactory;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "listenbrainz";

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync()
    {
        var token = await _settingsService.GetListenBrainzUserTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token)) return false;
        return await _settingsService.GetListenBrainzScrobblingEnabledAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ProcessPendingListensAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        if (!await IsEnabledAsync().ConfigureAwait(false)) return;

        var enabledSince = await _settingsService.GetListenBrainzEnabledSinceUtcAsync().ConfigureAwait(false);
        if (!enabledSince.HasValue) return;

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var pending = await ctx.ListenHistory
            .AsTracking()
            .Where(lh => lh.IsEligibleForScrobbling
                         && !lh.IsSubmittedToListenBrainz
                         && lh.ListenTimestampUtc >= enabledSince.Value)
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
                outcome = await SubmitAsync(entry.Song, "single", ToUnixSeconds(entry.ListenTimestampUtc))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ListenBrainz queue submission raised. Will retry later. Song '{Title}'.", entry.Song.Title);
                break;
            }

            if (outcome == SubmissionOutcome.TransientFailure)
            {
                _logger.LogDebug("ListenBrainz transient failure for '{Title}'. Stopping to preserve order.",
                    entry.Song.Title);
                break;
            }

            // Mark permanent rejections as submitted too so a single bad entry cannot block
            // every newer listen. Only transient failures keep the entry pending.
            if (outcome == SubmissionOutcome.PermanentFailure)
                _logger.LogWarning("ListenBrainz permanently rejected '{Title}'; dropping from queue.",
                    entry.Song.Title);

            entry.IsSubmittedToListenBrainz = true;
            changedCount++;
        }

        if (changedCount > 0)
            await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // Ordered so default(SubmissionOutcome) == TransientFailure. This matters because
    // HttpRetryHelper.ExecuteWithRetryAsync returns default(T) when retries are exhausted
    // or a transient exception is caught — which is exactly what TransientFailure means here.
    private enum SubmissionOutcome
    {
        TransientFailure = 0,
        Success,
        PermanentFailure
    }

    public async Task<bool> UpdateNowPlayingAsync(Song song) =>
        await SubmitAsync(song, "playing_now", listenedAt: null).ConfigureAwait(false) == SubmissionOutcome.Success;

    public async Task<bool> SubmitListenAsync(Song song, DateTime playStartTimeUtc) =>
        await SubmitAsync(song, "single", ToUnixSeconds(playStartTimeUtc)).ConfigureAwait(false)
            == SubmissionOutcome.Success;

    private static long ToUnixSeconds(DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
            : timestamp.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeSeconds();
    }

    public async Task<ValidateTokenResult> ValidateTokenAsync(string token, string? serverUrl = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new ValidateTokenResult(false, null, "Token is empty.");

        string baseUrl;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            baseUrl = DefaultBaseUrl;
        }
        else
        {
            var sanitized = SanitizeBaseUrl(serverUrl);
            if (sanitized is null)
                return new ValidateTokenResult(false, null, "Server URL must be an absolute http(s) URL.");
            baseUrl = sanitized;
        }
        var url = $"{baseUrl}{ValidateTokenPath}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new ValidateTokenResult(false, null, "Token is not valid.");

            if (!response.IsSuccessStatusCode)
                return new ValidateTokenResult(false, null, $"ListenBrainz returned {(int)response.StatusCode}.");

            ListenBrainzValidateTokenResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ListenBrainzValidateTokenResponse>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse ListenBrainz validate-token response.");
                return new ValidateTokenResult(false, null, "Could not parse response.");
            }

            if (parsed is null)
                return new ValidateTokenResult(false, null, "Could not parse response.");

            return parsed.Valid
                ? new ValidateTokenResult(true, parsed.UserName, null)
                : new ValidateTokenResult(false, null, parsed.Message ?? "Token is not valid.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ListenBrainz token validation failed.");
            return new ValidateTokenResult(false, null, "Could not reach ListenBrainz.");
        }
    }

    private async Task<SubmissionOutcome> SubmitAsync(Song song, string listenType, long? listenedAt)
    {
        var artist = song.PrimaryArtistName;
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(song.Title))
        {
            _logger.LogDebug("Skipping ListenBrainz '{ListenType}' - missing artist or title.", listenType);
            return SubmissionOutcome.PermanentFailure;
        }

        var token = await _settingsService.GetListenBrainzUserTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Cannot submit to ListenBrainz; user token is not configured.");
            return SubmissionOutcome.TransientFailure;
        }

        var baseUrl = await ResolveBaseUrlAsync().ConfigureAwait(false);

        var payload = new ListenBrainzSubmitPayload
        {
            ListenType = listenType,
            Payload = new List<ListenBrainzListen>
            {
                new()
                {
                    ListenedAt = listenedAt,
                    TrackMetadata = new ListenBrainzTrackMetadata
                    {
                        ArtistName = artist,
                        TrackName = song.Title,
                        ReleaseName = song.Album?.Title
                    }
                }
            }
        };

        var url = $"{baseUrl}{SubmitListensPath}";
        var operationName = $"ListenBrainz {listenType}";

        var result = await HttpRetryHelper.ExecuteWithRetryAsync<SubmissionOutcome>(
            async attempt =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOptions),
                    Encoding.UTF8,
                    "application/json");

                _logger.LogDebug("Calling ListenBrainz '{ListenType}' (Attempt {Attempt}/{MaxRetries})",
                    listenType, attempt, MaxRetries);

                using var response = await _httpClient.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return RetryResult<SubmissionOutcome>.Success(SubmissionOutcome.Success);

                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogWarning(
                    "ListenBrainz '{ListenType}' failed. Status: {StatusCode}, Response: {ResponseContent}. Attempt {Attempt}/{MaxRetries}",
                    listenType, response.StatusCode, body, attempt, MaxRetries);

                if (HttpRetryHelper.IsRetryableStatusCode(response.StatusCode))
                    return RetryResult<SubmissionOutcome>.TransientFailure();

                return RetryResult<SubmissionOutcome>.Success(SubmissionOutcome.PermanentFailure);
            },
            _logger,
            operationName,
            CancellationToken.None
        ).ConfigureAwait(false);

        return result;
    }

    private async Task<string> ResolveBaseUrlAsync()
    {
        var configured = await _settingsService.GetListenBrainzServerUrlAsync().ConfigureAwait(false);
        var sanitized = SanitizeBaseUrl(configured);
        if (sanitized is not null) return sanitized;

        if (!string.IsNullOrWhiteSpace(configured))
            _logger.LogWarning(
                "Configured ListenBrainz server URL is not a valid http(s) URL; falling back to default.");
        return DefaultBaseUrl;
    }

    /// <summary>
    ///     Returns the URL trimmed of any trailing slash if it is an absolute http or https URL.
    ///     Plain http is only accepted for loopback hosts to avoid leaking the user's token over
    ///     cleartext. Returns null for any other input, including null/whitespace.
    /// </summary>
    private static string? SanitizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps &&
            !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            return null;
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }
}
