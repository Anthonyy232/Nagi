using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Service for fetching synchronized lyrics from NetEase Cloud Music.
///     Uses unofficial API endpoints commonly used by open-source music players.
/// </summary>
public partial class NetEaseLyricsService : INetEaseLyricsService
{
    private const string SearchUrl = "https://music.163.com/api/search/get";
    private const string LyricsUrl = "https://music.163.com/api/song/lyric";
    private const int MaxRetries = 3;
    private const int RateLimitDelayMultiplier = 5;

    private readonly HttpClient _httpClient;
    private readonly ILogger<NetEaseLyricsService> _logger;

    private volatile bool _isApiDisabled;

    public NetEaseLyricsService(IHttpClientFactory httpClientFactory, ILogger<NetEaseLyricsService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://music.163.com/");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> SearchLyricsAsync(string trackName, string? artistName, 
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return null;

        if (_isApiDisabled)
        {
            _logger.LogDebug("NetEase API is disabled for this session.");
            return null;
        }

        try
        {
            // Build search query
            var query = string.IsNullOrWhiteSpace(artistName) 
                ? trackName 
                : $"{trackName} {artistName}";

            var songId = await SearchSongAsync(query, trackName, artistName, cancellationToken).ConfigureAwait(false);
            if (songId is null)
            {
                _logger.LogDebug("No NetEase match found for: {Query}", query);
                return null;
            }

            var lyrics = await GetLyricsAsync(songId.Value, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(lyrics))
            {
                _logger.LogDebug("No lyrics found for NetEase song ID: {SongId}", songId);
                return null;
            }

            _logger.LogInformation("Found NetEase lyrics for: {TrackName}", trackName);
            return lyrics;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error fetching NetEase lyrics for: {TrackName}", trackName);
            return null;
        }
    }

    private async Task<long?> SearchSongAsync(string query, string trackName, string? artistName, CancellationToken cancellationToken)
    {
        var operationName = $"NetEase search for {query}";

        return await HttpRetryHelper.ExecuteWithRetryAsync<long?>(
            async attempt =>
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["s"] = query,
                    ["type"] = "1", // 1 = songs
                    ["limit"] = "10", // Request more results for better matching
                    ["offset"] = "0"
                });

                _logger.LogDebug("Searching NetEase for: {Query} (Attempt {Attempt}/{MaxRetries})", query, attempt, MaxRetries);

                using var response = await _httpClient.PostAsync(SearchUrl, content, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning("NetEase rate limited or blocked. Attempt {Attempt}/{MaxRetries}", attempt, MaxRetries);
                    if (attempt >= MaxRetries)
                    {
                        _logger.LogError("NetEase blocked repeatedly. Disabling for this session.");
                        _isApiDisabled = true;
                    }
                    return RetryResult<long?>.RateLimitFailure(RateLimitDelayMultiplier);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("NetEase search failed with status {Status}. Attempt {Attempt}/{MaxRetries}", 
                        response.StatusCode, attempt, MaxRetries);
                    return RetryResult<long?>.FromHttpStatus(response.StatusCode);
                }

                var result = await response.Content.ReadFromJsonAsync<NetEaseSearchResult>(
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var songs = result?.Result?.Songs;
                if (songs is null || songs.Count == 0)
                    return RetryResult<long?>.SuccessEmpty();

                // Find best match in a single pass - O(n) with only one allocation per song
                // Score breakdown: TrackExact=4, TrackContains=2, ArtistMatch=1
                var bestMatch = songs
                    .Select(s =>
                    {
                        var trackExact = s.Name != null && s.Name.Equals(trackName, StringComparison.OrdinalIgnoreCase);
                        var trackContains = s.Name != null && s.Name.Contains(trackName, StringComparison.OrdinalIgnoreCase);
                        var artistMatch = !string.IsNullOrWhiteSpace(artistName) &&
                                          s.Artists?.Any(a => a.Name != null &&
                                              a.Name.Contains(artistName, StringComparison.OrdinalIgnoreCase)) == true;

                        // Skip songs with no track match at all
                        if (!trackExact && !trackContains)
                            return (Song: (NetEaseSong?)null, Score: -1);

                        var score = (trackExact ? 4 : 0) + (trackContains ? 2 : 0) + (artistMatch ? 1 : 0);
                        return (Song: (NetEaseSong?)s, Score: score);
                    })
                    .Where(x => x.Song != null)
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                return bestMatch.Song != null
                    ? RetryResult<long?>.Success(bestMatch.Song.Id)
                    : RetryResult<long?>.SuccessEmpty();
            },
            _logger,
            operationName,
            cancellationToken,
            MaxRetries
        ).ConfigureAwait(false);
    }

    private async Task<string?> GetLyricsAsync(long songId, CancellationToken cancellationToken)
    {
        var operationName = $"NetEase lyrics fetch for ID {songId}";

        return await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                var url = $"{LyricsUrl}?id={songId}&lv=1";
                _logger.LogDebug("Fetching NetEase lyrics for song ID: {SongId} (Attempt {Attempt}/{MaxRetries})", 
                    songId, attempt, MaxRetries);

                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

                // Check for rate limiting on lyrics endpoint too
                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning("NetEase rate limited on lyrics fetch. Attempt {Attempt}/{MaxRetries}", attempt, MaxRetries);
                    if (attempt >= MaxRetries)
                    {
                        _logger.LogError("NetEase rate limited repeatedly on lyrics fetch. Disabling for this session.");
                        _isApiDisabled = true;
                    }
                    return RetryResult<string>.RateLimitFailure(RateLimitDelayMultiplier);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("NetEase lyrics fetch failed with status {Status}. Attempt {Attempt}/{MaxRetries}", 
                        response.StatusCode, attempt, MaxRetries);
                    return RetryResult<string>.FromHttpStatus(response.StatusCode);
                }

                var result = await response.Content.ReadFromJsonAsync<NetEaseLyricsResult>(
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var lrcContent = result?.Lrc?.Lyric;
                if (string.IsNullOrWhiteSpace(lrcContent))
                    return RetryResult<string>.SuccessEmpty();

                // Validate that it's actually LRC format (has timestamps)
                if (!LrcTimestampRegex().IsMatch(lrcContent))
                    return RetryResult<string>.SuccessEmpty();

                return RetryResult<string>.Success(lrcContent);
            },
            _logger,
            operationName,
            cancellationToken,
            MaxRetries
        ).ConfigureAwait(false);
    }

    [GeneratedRegex(@"\[\d{2}:\d{2}")]
    private static partial Regex LrcTimestampRegex();

    // DTOs for JSON deserialization
    private sealed class NetEaseSearchResult
    {
        [JsonPropertyName("result")]
        public NetEaseSearchResultData? Result { get; set; }
    }

    private sealed class NetEaseSearchResultData
    {
        [JsonPropertyName("songs")]
        public List<NetEaseSong>? Songs { get; set; }
    }

    private sealed class NetEaseSong
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("artists")]
        public List<NetEaseArtist>? Artists { get; set; }
    }

    private sealed class NetEaseArtist
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class NetEaseLyricsResult
    {
        [JsonPropertyName("lrc")]
        public NetEaseLyric? Lrc { get; set; }
    }

    private sealed class NetEaseLyric
    {
        [JsonPropertyName("lyric")]
        public string? Lyric { get; set; }
    }
}
