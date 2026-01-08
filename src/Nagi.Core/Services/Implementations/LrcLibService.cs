using System.Net;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http;
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
    private const int MaxRetries = 3;
    private const int RateLimitDelayMultiplier = 5;
    
    private readonly HttpClient _httpClient;
    private readonly ILogger<LrcLibService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private volatile bool _isRateLimited;

    public LrcLibService(IHttpClientFactory httpClientFactory, ILogger<LrcLibService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetLyricsAsync(string trackName, string? artistName, string? albumName, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackName))
        {
            return null;
        }

        if (_isRateLimited)
        {
            _logger.LogDebug("Skipping lyrics fetch - rate limited.");
            return null;
        }

        try
        {
            // Placeholders used when metadata is missing
            const string unknownArtist = "Unknown Artist";
            const string unknownAlbum = "Unknown Album";

            var hasValidArtist = !string.IsNullOrWhiteSpace(artistName) &&
                                 !artistName.Equals(unknownArtist, StringComparison.OrdinalIgnoreCase);
            var hasValidAlbum = !string.IsNullOrWhiteSpace(albumName) &&
                                 !albumName.Equals(unknownAlbum, StringComparison.OrdinalIgnoreCase);

            // Fire both requests in parallel for faster resolution when nothing is found.
            // We prefer the strict result if it succeeds; otherwise use search.
            Task<string?>? strictTask = null;
            var searchTask = SearchLyricsAsync(trackName, artistName, albumName, duration, cancellationToken);

            // 1. Try Strict Match /api/get (Requires Track + Artist + Album)
            // If any are missing or placeholders, we skip directly to search.
            if (hasValidArtist && hasValidAlbum)
            {
                strictTask = TryStrictLookupAsync(trackName, artistName!, albumName!, duration, cancellationToken);
            }
            else
            {
                _logger.LogDebug("Strict lookup skipped (missing artist or album). Using search for: {Track}", trackName);
            }

            // If strict lookup is possible, await it first (preferred source)
            if (strictTask != null)
            {
                var strictResult = await strictTask.ConfigureAwait(false);
                if (!string.IsNullOrEmpty(strictResult))
                {
                    // Strict succeeded - observe (but don't wait for) the search task to prevent unobserved exceptions
                    _ = searchTask.ContinueWith(
                        static t => { _ = t.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted);
                    return strictResult;
                }
            }

            // 2. Search Fallback - strict was skipped, returned null, or returned empty
            return await searchTask.ConfigureAwait(false);
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
        var operationName = $"LRCLIB strict lookup for {artistName} - {trackName}";

        return await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                var query = HttpUtility.ParseQueryString(string.Empty);
                query["track_name"] = trackName;
                query["artist_name"] = artistName;
                query["album_name"] = albumName;
                query["duration"] = duration.TotalSeconds.ToString("F0");

                var requestUrl = $"{BaseUrl}?{query}";
                _logger.LogDebug("Fetching lyrics strict lookup from LRCLIB for: {Artist} - {Track} (Attempt {Attempt}/{MaxRetries})", 
                    artistName, trackName, attempt, MaxRetries);

                using var response = await _httpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var lrcResponse = JsonSerializer.Deserialize<LrcLibResponse>(content, _jsonOptions);

                    return !string.IsNullOrEmpty(lrcResponse?.SyncedLyrics)
                        ? RetryResult<string>.Success(lrcResponse.SyncedLyrics)
                        : RetryResult<string>.SuccessEmpty();
                }
                
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("LRCLIB rate limit hit in strict lookup. Attempt {Attempt}/{MaxRetries}", attempt, MaxRetries);
                    if (attempt >= MaxRetries)
                    {
                        _logger.LogWarning("LRCLIB rate limit hit repeatedly. Disabling for this session.");
                        _isRateLimited = true;
                    }
                    return RetryResult<string>.RateLimitFailure(RateLimitDelayMultiplier);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return RetryResult<string>.SuccessEmpty();

                _logger.LogWarning("Strict lookup failed with status {Status}. Attempt {Attempt}/{MaxRetries}", 
                    response.StatusCode, attempt, MaxRetries);
                
                return RetryResult<string>.FromHttpStatus(response.StatusCode);
            },
            _logger,
            operationName,
            cancellationToken,
            MaxRetries
        ).ConfigureAwait(false);
    }

    private async Task<string?> SearchLyricsAsync(string trackName, string? artistName, string? albumName, TimeSpan duration, CancellationToken cancellationToken)
    {
        var operationName = $"LRCLIB search for {artistName} - {trackName}";

        return await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                var query = HttpUtility.ParseQueryString(string.Empty);
                query["track_name"] = trackName;
                query["artist_name"] = artistName;
                // Note: Not sending album_name to search to be more permissive, we will filter locally.
                
                var requestUrl = $"{SearchUrl}?{query}";
                _logger.LogDebug("Searching lyrics on LRCLIB for: {Artist} - {Track} (Attempt {Attempt}/{MaxRetries})", 
                    artistName, trackName, attempt, MaxRetries);

                using var response = await _httpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
                
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("LRCLIB rate limit hit during search. Attempt {Attempt}/{MaxRetries}", attempt, MaxRetries);
                    if (attempt >= MaxRetries)
                    {
                        _logger.LogWarning("LRCLIB rate limit hit repeatedly during search. Disabling for this session.");
                        _isRateLimited = true;
                    }
                    return RetryResult<string>.RateLimitFailure(RateLimitDelayMultiplier);
                }
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("LRCLIB search failed with status {Status}. Attempt {Attempt}/{MaxRetries}", 
                        response.StatusCode, attempt, MaxRetries);
                    
                    return RetryResult<string>.FromHttpStatus(response.StatusCode);
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var searchResults = JsonSerializer.Deserialize<List<LrcLibResponse>>(content, _jsonOptions);

                if (searchResults is null || searchResults.Count == 0)
                {
                    _logger.LogDebug("No search results found for: {Artist} - {Track}", artistName, trackName);
                    return RetryResult<string>.SuccessEmpty();
                }

                // Client-side filtering as per user requirements:
                // 1. Must have synced lyrics
                // 2. Duration flexible (+/- 30 seconds)
                // 3. Prefer album match, but not required
                // 4. Closest duration tie-breaker

                var targetDurationSeconds = duration.TotalSeconds;

                var bestMatch = searchResults
                    .Where(r => !string.IsNullOrEmpty(r.SyncedLyrics))
                    .Where(r => Math.Abs(r.Duration - targetDurationSeconds) <= 30)
                    .OrderBy(r =>  // Primary sort: Album match preference (if we have an album to check against)
                    {
                        if (string.IsNullOrWhiteSpace(albumName) || string.IsNullOrWhiteSpace(r.AlbumName))
                            return 1; // Treat as "no match" regarding sorting priority if info is missing
                        
                        return string.Equals(r.AlbumName, albumName, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                    })
                    .ThenBy(r => Math.Abs(r.Duration - targetDurationSeconds)) // Secondary sort: Closest duration
                    .FirstOrDefault();

                if (bestMatch != null)
                {
                    _logger.LogDebug("Found fallback lyrics via search for: {Artist} - {Track} (Match: {MatchTrack}, Duration Diff: {Diff}s)", 
                        artistName, trackName, bestMatch.TrackName, bestMatch.Duration - targetDurationSeconds);
                    return RetryResult<string>.Success(bestMatch.SyncedLyrics!);
                }
                
                _logger.LogDebug("Search results found but none matched criteria for: {Artist} - {Track}", artistName, trackName);
                return RetryResult<string>.SuccessEmpty();
            },
            _logger,
            operationName,
            cancellationToken,
            MaxRetries
        ).ConfigureAwait(false);
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
