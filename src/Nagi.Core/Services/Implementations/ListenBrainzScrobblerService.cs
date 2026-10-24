using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Http.Pipelines;
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
    private const string ClientName = "Nagi";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<ListenBrainzScrobblerService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IAppInfoService _appInfoService;

    public ListenBrainzScrobblerService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        IDbContextFactory<MusicDbContext> contextFactory,
        ISettingsService settingsService,
        IAppInfoService appInfoService,
        ILogger<ListenBrainzScrobblerService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _contextFactory = contextFactory;
        _settingsService = settingsService;
        _appInfoService = appInfoService;
        _logger = logger;
    }

    public string Id => ServiceProviderIds.ListenBrainz;

    public async Task<bool> IsEnabledAsync()
    {
        var token = await _settingsService.GetListenBrainzUserTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token)) return false;
        return await _settingsService.GetListenBrainzScrobblingEnabledAsync().ConfigureAwait(false);
    }

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
            .Include(lh => lh.Song).ThenInclude(s => s.Album)
            .OrderBy(lh => lh.ListenTimestampUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var chunk in pending.Chunk(100))
        {
            if (!await ProcessBatchAsync(ctx, chunk, cancellationToken).ConfigureAwait(false)) break;
        }
    }

    private async Task<bool> ProcessBatchAsync(MusicDbContext context, IReadOnlyList<ListenHistory> entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SubmissionOutcome outcome;
        try
        {
            outcome = await SubmitBatchAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ListenBrainz submission failed; retaining {Count} listens for retry.", entries.Count);
            return false;
        }

        if (outcome == SubmissionOutcome.TransientFailure) return false;
        if (outcome == SubmissionOutcome.PermanentFailure && entries.Count > 1)
        {
            // A single invalid listen must not discard every valid listen in its batch.
            foreach (var entry in entries)
                if (!await ProcessBatchAsync(context, [entry], cancellationToken).ConfigureAwait(false)) return false;
            return true;
        }

        if (outcome == SubmissionOutcome.PermanentFailure)
            _logger.LogWarning("ListenBrainz permanently rejected listen {Id}; dropping it from the queue.", entries[0].Id);
        foreach (var entry in entries)
            entry.IsSubmittedToListenBrainz = true;
        // Persist acknowledgements even if shutdown cancels further submissions.
        await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

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
            return new ValidateTokenResult(false, null, Resources.Strings.ListenBrainz_EmptyToken);

        string baseUrl;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            baseUrl = DefaultBaseUrl;
        }
        else
        {
            var sanitized = SanitizeBaseUrl(serverUrl);
            if (sanitized is null)
                return new ValidateTokenResult(false, null, Resources.Strings.ListenBrainz_InvalidServerUrl);
            baseUrl = sanitized;
        }
        var url = $"{baseUrl}{ValidateTokenPath}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new ValidateTokenResult(false, null, Resources.Strings.ListenBrainz_InvalidToken);

            if (!response.IsSuccessStatusCode)
                return new ValidateTokenResult(false, null, string.Format(Resources.Strings.ListenBrainz_ServerError, (int)response.StatusCode));

            var parsed = await response.Content.ReadFromJsonAsync<ListenBrainzValidateTokenResponse>(JsonOptions)
                .ConfigureAwait(false);

            if (parsed is null)
                return new ValidateTokenResult(false, null, Resources.Strings.ListenBrainz_InvalidResponse);

            return parsed.Valid
                ? new ValidateTokenResult(true, parsed.UserName, null)
                : new ValidateTokenResult(false, null, parsed.Message ?? Resources.Strings.ListenBrainz_InvalidToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse ListenBrainz validate-token response.");
            return new ValidateTokenResult(false, null, Resources.Strings.ListenBrainz_InvalidResponse);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ListenBrainz token validation failed.");
            return new ValidateTokenResult(false, null, Resources.Strings.ListenBrainz_Unreachable);
        }
    }

    private Task<SubmissionOutcome> SubmitAsync(Song song, string listenType, long? listenedAt)
    {
        var listen = CreateListen(song, listenedAt);
        return listen is null
            ? Task.FromResult(SubmissionOutcome.PermanentFailure)
            : PostPayloadAsync(new ListenBrainzSubmitPayload { ListenType = listenType, Payload = [listen] }, CancellationToken.None);
    }

    private Task<SubmissionOutcome> SubmitBatchAsync(IReadOnlyList<ListenHistory> entries, CancellationToken cancellationToken)
    {
        var listens = entries.Select(entry => CreateListen(entry.Song, ToUnixSeconds(entry.ListenTimestampUtc)))
            .OfType<ListenBrainzListen>().ToList();
        return listens.Count == 0
            ? Task.FromResult(SubmissionOutcome.PermanentFailure)
            : PostPayloadAsync(new ListenBrainzSubmitPayload
            {
                ListenType = listens.Count == 1 ? "single" : "import", Payload = listens
            }, cancellationToken);
    }

    private ListenBrainzListen? CreateListen(Song song, long? listenedAt)
    {
        if (string.IsNullOrWhiteSpace(song.PrimaryArtistName) || string.IsNullOrWhiteSpace(song.Title))
            return null;

        return new ListenBrainzListen
        {
            ListenedAt = listenedAt,
            TrackMetadata = new ListenBrainzTrackMetadata
            {
                ArtistName = song.PrimaryArtistName,
                TrackName = song.Title,
                ReleaseName = song.Album?.Title,
                AdditionalInfo = new ListenBrainzAdditionalInfo
                {
                    RecordingMbid = song.MusicBrainzTrackId,
                    ReleaseMbid = song.MusicBrainzReleaseId,
                    ReleaseGroupMbid = song.MusicBrainzReleaseGroupId,
                    TrackNumber = song.TrackNumber,
                    DurationMs = song.Duration > TimeSpan.Zero ? (long)song.Duration.TotalMilliseconds : null,
                    MediaPlayer = ClientName,
                    SubmissionClient = ClientName,
                    SubmissionClientVersion = _appInfoService.GetAppVersion()
                }
            }
        };
    }

    private async Task<SubmissionOutcome> PostPayloadAsync(
        ListenBrainzSubmitPayload payload, CancellationToken cancellationToken)
    {
        var token = await _settingsService.GetListenBrainzUserTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Cannot submit to ListenBrainz; user token is not configured.");
            return SubmissionOutcome.TransientFailure;
        }

        var baseUrl = await ResolveBaseUrlAsync().ConfigureAwait(false);
        var url = $"{baseUrl}{SubmitListensPath}";

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.ListenBrainz,
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
                request.Content = JsonContent.Create(payload, options: JsonOptions);
                _logger.LogDebug("Calling ListenBrainz '{ListenType}' with {Count} listen(s).",
                    payload.ListenType, payload.Payload.Count);
                return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (response.IsSuccessStatusCode)
                    return SubmissionOutcome.Success;

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "ListenBrainz '{ListenType}' failed. Status: {StatusCode}, Response: {ResponseContent}.",
                    payload.ListenType, response.StatusCode, body);

                return response.StatusCode == HttpStatusCode.BadRequest
                    ? SubmissionOutcome.PermanentFailure
                    : SubmissionOutcome.TransientFailure;
            },
            fallback: SubmissionOutcome.TransientFailure,
            _logger,
            $"submit '{payload.ListenType}'",
            cancellationToken).ConfigureAwait(false);
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
