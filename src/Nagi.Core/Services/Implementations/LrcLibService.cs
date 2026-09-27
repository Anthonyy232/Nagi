using System.Net;
using System.Globalization;
using System.Net.Http.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Polly.CircuitBreaker;

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

        trackName = ArtistNameHelper.NormalizeStringCore(trackName)!;
        artistName = ArtistNameHelper.NormalizeStringCore(artistName);
        albumName = ArtistNameHelper.NormalizeStringCore(albumName);
        if (string.Equals(artistName, Artist.UnknownArtistName, StringComparison.OrdinalIgnoreCase)) artistName = null;
        if (string.Equals(albumName, Album.UnknownAlbumName, StringComparison.OrdinalIgnoreCase)) albumName = null;

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.LrcLib))
        {
            _logger.LogDebug("LRCLIB circuit is open; skipping lyrics fetch.");
            throw new BrokenCircuitException("LRCLIB is temporarily unavailable.");
        }

        try
        {
            // Try strict lookup first when artist+album are known. Only fall through to search
            // if strict misses — saves a round-trip + a rate-limit permit on the happy path.
            if (artistName is not null && albumName is not null)
            {
                var strictResult = await TryStrictLookupAsync(trackName, artistName, albumName, duration, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(strictResult))
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
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch lyrics from LRCLIB for {Artist} - {Track}", artistName, trackName);
            throw;
        }
    }

    private async Task<string?> TryStrictLookupAsync(string trackName, string artistName, string albumName, TimeSpan duration, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["track_name"] = trackName;
        query["artist_name"] = artistName;
        query["album_name"] = albumName;
        query["duration"] = duration.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture);

        var requestUrl = $"{BaseUrl}?{query}";

        using var response = await _pipelines.ExecuteAsync(
            ServiceProviderIds.LrcLib,
            async ct =>
            {
                _logger.LogDebug("Fetching lyrics strict lookup from LRCLIB for: {Artist} - {Track}", artistName, trackName);
                return await _httpClient.GetAsync(requestUrl, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var lrcResponse = await response.Content.ReadFromJsonAsync<LrcLibResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return lrcResponse?.SyncedLyrics;
    }

    private async Task<string?> SearchLyricsAsync(string trackName, string? artistName, string? albumName, TimeSpan duration, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["track_name"] = trackName;
        if (artistName is not null) query["artist_name"] = artistName;
        // Not sending album_name to search to be more permissive; filter locally below.

        var requestUrl = $"{SearchUrl}?{query}";

        using var response = await _pipelines.ExecuteAsync(
            ServiceProviderIds.LrcLib,
            async ct =>
            {
                _logger.LogDebug("Searching lyrics on LRCLIB for: {Artist} - {Track}", artistName, trackName);
                return await _httpClient.GetAsync(requestUrl, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var searchResults = await response.Content.ReadFromJsonAsync<List<LrcLibResponse>>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (searchResults is null || searchResults.Count == 0)
        {
            _logger.LogDebug("No search results found for: {Artist} - {Track}", artistName, trackName);
            return null;
        }

        // Client-side filtering: must have synced lyrics, ±30s duration tolerance, prefer
        // album match, closest duration tie-breaker.
        var targetDurationSeconds = duration.TotalSeconds;
        var bestMatch = searchResults
            .Where(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
            .Where(r => Math.Abs(r.Duration - targetDurationSeconds) <= 30)
            .OrderByDescending(r => albumName is not null && string.Equals(r.AlbumName, albumName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => Math.Abs(r.Duration - targetDurationSeconds))
            .FirstOrDefault();

        if (bestMatch != null)
        {
            _logger.LogDebug("Found fallback lyrics via search for: {Artist} - {Track}", artistName, trackName);
            return bestMatch.SyncedLyrics;
        }

        _logger.LogDebug("Search results found but none matched criteria for: {Artist} - {Track}", artistName, trackName);
        return null;
    }

    private sealed class LrcLibResponse
    {
        public string? AlbumName { get; set; }
        public double Duration { get; set; }
        public string? SyncedLyrics { get; set; }
    }
}
