using System.Net;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
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
    
    // LRCLIB requires duration in seconds
    private readonly HttpClient _httpClient;
    private readonly ILogger<LrcLibService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LrcLibService(IHttpClientFactory httpClientFactory, ILogger<LrcLibService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetLyricsAsync(string trackName, string artistName, string albumName, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(trackName))
        {
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

            // 1. Try Strict Match /api/get (Requires Track + Artist + Album)
            // If any are missing or placeholders, we skip directly to search.
            if (hasValidArtist && hasValidAlbum)
            {
                var query = HttpUtility.ParseQueryString(string.Empty);
                query["track_name"] = trackName;
                query["artist_name"] = artistName;
                query["album_name"] = albumName;
                query["duration"] = duration.TotalSeconds.ToString("F0");

                var requestUrl = $"{BaseUrl}?{query}";
                _logger.LogInformation("Fetching lyrics strict lookup from LRCLIB for: {Artist} - {Track}", artistName, trackName);

                using var response = await _httpClient.GetAsync(requestUrl);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var lrcResponse = JsonSerializer.Deserialize<LrcLibResponse>(content, _jsonOptions);

                    if (!string.IsNullOrEmpty(lrcResponse?.SyncedLyrics))
                    {
                        return lrcResponse.SyncedLyrics;
                    }
                }
                else if (response.StatusCode != HttpStatusCode.NotFound)
                {
                     // Log other errors but proceed to fallback just in case
                     _logger.LogWarning("Strict lookup failed with status {Status}. Proceeding to search.", response.StatusCode);
                }
            }
            else
            {
                 _logger.LogInformation("Strict lookup skipped (missing artist or album). Proceeding to search for: {Track}", trackName);
            }

            // 2. Search Fallback /api/search
            _logger.LogInformation("Attempting search fallback for: {Track}", trackName);
            return await SearchLyricsAsync(trackName, artistName, albumName, duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch lyrics from LRCLIB for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    private async Task<string?> SearchLyricsAsync(string trackName, string artistName, string albumName, TimeSpan duration)
    {
        try
        {
            var query = HttpUtility.ParseQueryString(string.Empty);
            query["track_name"] = trackName;
            query["artist_name"] = artistName;
            // Note: Not sending album_name to search to be more permissive, we will filter locally.
            
            var requestUrl = $"{SearchUrl}?{query}";
            using var response = await _httpClient.GetAsync(requestUrl);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var searchResults = JsonSerializer.Deserialize<List<LrcLibResponse>>(content, _jsonOptions);

            if (searchResults is null || searchResults.Count == 0)
            {
                _logger.LogInformation("No search results found for: {Artist} - {Track}", artistName, trackName);
                return null;
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
                _logger.LogInformation("Found fallback lyrics via search for: {Artist} - {Track} (Match: {MatchTrack}, Duration Diff: {Diff}s)", 
                    artistName, trackName, bestMatch.TrackName, bestMatch.Duration - targetDurationSeconds);
                return bestMatch.SyncedLyrics;
            }
            
            _logger.LogInformation("Search results found but none matched criteria for: {Artist} - {Track}", artistName, trackName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search lyrics fallback for {Artist} - {Track}", artistName, trackName);
            return null;
        }
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
