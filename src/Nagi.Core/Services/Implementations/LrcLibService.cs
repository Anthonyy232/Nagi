using System.Net;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Implementation of <see cref="IOnlineLyricsService" /> using the LRCLIB API.
///     See: https://lrclib.net/docs
/// </summary>
public class LrcLibService : IOnlineLyricsService
{
    private const string BaseUrl = "https://lrclib.net/api/get";
    private const string SearchUrl = "https://lrclib.net/api/search";
    private const string UserAgent = "Nagi/1.0 (https://github.com/Anthonyy232/Nagi)";

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<LrcLibService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LrcLibService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        ILogger<LrcLibService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _pipelines = pipelines;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetLyricsAsync(string trackName, string? artistName, string? albumName, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return null;

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.LrcLib))
        {
            _logger.LogDebug("LRCLIB circuit is open; skipping lyrics fetch.");
            return null;
        }

        try
        {
            var hasValidArtist = !string.IsNullOrWhiteSpace(artistName) &&
                                 !artistName.Equals(Artist.UnknownArtistName, StringComparison.OrdinalIgnoreCase);
            var hasValidAlbum = !string.IsNullOrWhiteSpace(albumName) &&
                                 !albumName.Equals(Album.UnknownAlbumName, StringComparison.OrdinalIgnoreCase);

            // Try strict lookup first when artist+album are known. Only fall through to search
            // if strict misses — saves a round-trip + a rate-limit permit on the happy path.
            if (hasValidArtist && hasValidAlbum)
            {
                var strictResult = await TryStrictLookupAsync(trackName, artistName!, albumName!, duration, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(strictResult))
                    return strictResult;
            }
            else
            {
                _logger.LogDebug("Strict lookup skipped (missing artist or album). Using search for: {Track}", trackName);
            }

            return await SearchLyricsAsync(trackName, artistName, albumName, duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Lyrics fetch cancelled for {Artist} - {Track}", artistName, trackName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch lyrics from LRCLIB for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    private async Task<string?> TryStrictLookupAsync(string trackName, string artistName, string albumName, TimeSpan duration, CancellationToken cancellationToken)
    {
        var normalizedTrack = ArtistNameHelper.NormalizeStringCore(trackName) ?? trackName;
        var normalizedArtist = ArtistNameHelper.NormalizeStringCore(artistName) ?? artistName;
        var normalizedAlbum = ArtistNameHelper.NormalizeStringCore(albumName) ?? albumName;

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["track_name"] = normalizedTrack;
        query["artist_name"] = normalizedArtist;
        query["album_name"] = normalizedAlbum;
        query["duration"] = duration.TotalSeconds.ToString("F0");

        var requestUrl = $"{BaseUrl}?{query}";

        return await _pipelines.ExecuteWithFallbackAsync<string?>(
            ServiceProviderIds.LrcLib,
            async ct =>
            {
                _logger.LogDebug("Fetching lyrics strict lookup from LRCLIB for: {Artist} - {Track}", artistName, trackName);
                return await _httpClient.GetAsync(requestUrl, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var lrcResponse = JsonSerializer.Deserialize<LrcLibResponse>(content, _jsonOptions);
                    return !string.IsNullOrEmpty(lrcResponse?.SyncedLyrics) ? lrcResponse.SyncedLyrics : null;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                _logger.LogWarning("Strict lookup failed with status {Status}.", response.StatusCode);
                return null;
            },
            fallback: null,
            _logger,
            $"strict lookup for {artistName} - {trackName}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> SearchLyricsAsync(string trackName, string? artistName, string? albumName, TimeSpan duration, CancellationToken cancellationToken)
    {
        var normalizedTrack = ArtistNameHelper.NormalizeStringCore(trackName) ?? trackName;
        var normalizedArtist = ArtistNameHelper.NormalizeStringCore(artistName) ?? artistName;

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["track_name"] = normalizedTrack;
        query["artist_name"] = normalizedArtist;
        // Not sending album_name to search to be more permissive; filter locally below.

        var requestUrl = $"{SearchUrl}?{query}";

        return await _pipelines.ExecuteWithFallbackAsync<string?>(
            ServiceProviderIds.LrcLib,
            async ct =>
            {
                _logger.LogDebug("Searching lyrics on LRCLIB for: {Artist} - {Track}", artistName, trackName);
                return await _httpClient.GetAsync(requestUrl, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("LRCLIB search failed with status {Status}.", response.StatusCode);
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var searchResults = JsonSerializer.Deserialize<List<LrcLibResponse>>(content, _jsonOptions);

                if (searchResults is null || searchResults.Count == 0)
                {
                    _logger.LogDebug("No search results found for: {Artist} - {Track}", artistName, trackName);
                    return null;
                }

                // Client-side filtering: must have synced lyrics, ±30s duration tolerance, prefer
                // album match, closest duration tie-breaker.
                var targetDurationSeconds = duration.TotalSeconds;
                var bestMatch = searchResults
                    .Where(r => !string.IsNullOrEmpty(r.SyncedLyrics))
                    .Where(r => Math.Abs(r.Duration - targetDurationSeconds) <= 30)
                    .OrderBy(r =>
                    {
                        if (string.IsNullOrWhiteSpace(albumName) || string.IsNullOrWhiteSpace(r.AlbumName))
                            return 1;
                        return string.Equals(r.AlbumName, albumName, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                    })
                    .ThenBy(r => Math.Abs(r.Duration - targetDurationSeconds))
                    .FirstOrDefault();

                if (bestMatch != null)
                {
                    _logger.LogDebug("Found fallback lyrics via search for: {Artist} - {Track}", artistName, trackName);
                    return bestMatch.SyncedLyrics;
                }

                _logger.LogDebug("Search results found but none matched criteria for: {Artist} - {Track}", artistName, trackName);
                return null;
            },
            fallback: null,
            _logger,
            $"search for {artistName} - {trackName}",
            cancellationToken).ConfigureAwait(false);
    }

    private class LrcLibResponse
    {
        public long Id { get; set; }
        public string? TrackName { get; set; }
        public string? ArtistName { get; set; }
        public string? AlbumName { get; set; }
        public double Duration { get; set; }
        public bool Instrumental { get; set; }
        public string? SyncedLyrics { get; set; }
        public string? PlainLyrics { get; set; }
    }
}
