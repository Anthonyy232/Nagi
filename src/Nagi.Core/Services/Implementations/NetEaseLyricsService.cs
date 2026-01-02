using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
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

    private readonly HttpClient _httpClient;
    private readonly ILogger<NetEaseLyricsService> _logger;

    private bool _isApiDisabled;

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

            var songId = await SearchSongAsync(query, cancellationToken);
            if (songId is null)
            {
                _logger.LogDebug("No NetEase match found for: {Query}", query);
                return null;
            }

            var lyrics = await GetLyricsAsync(songId.Value, cancellationToken);
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

    private async Task<long?> SearchSongAsync(string query, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["s"] = query,
            ["type"] = "1", // 1 = songs
            ["limit"] = "1",
            ["offset"] = "0"
        });

        using var response = await _httpClient.PostAsync(SearchUrl, content, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
            response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("NetEase rate limited or blocked. Disabling for this session.");
            _isApiDisabled = true;
            return null;
        }

        if (!response.IsSuccessStatusCode)
            return null;

        var result = await response.Content.ReadFromJsonAsync<NetEaseSearchResult>(
            cancellationToken: cancellationToken);

        return result?.Result?.Songs?.FirstOrDefault()?.Id;
    }

    private async Task<string?> GetLyricsAsync(long songId, CancellationToken cancellationToken)
    {
        var url = $"{LyricsUrl}?id={songId}&lv=1";
        using var response = await _httpClient.GetAsync(url, cancellationToken);

        // Check for rate limiting on lyrics endpoint too
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
            response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("NetEase rate limited on lyrics fetch. Disabling for this session.");
            _isApiDisabled = true;
            return null;
        }

        if (!response.IsSuccessStatusCode)
            return null;

        var result = await response.Content.ReadFromJsonAsync<NetEaseLyricsResult>(
            cancellationToken: cancellationToken);

        var lrcContent = result?.Lrc?.Lyric;
        if (string.IsNullOrWhiteSpace(lrcContent))
            return null;

        // Validate that it's actually LRC format (has timestamps)
        if (!LrcTimestampRegex().IsMatch(lrcContent))
            return null;

        return lrcContent;
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
