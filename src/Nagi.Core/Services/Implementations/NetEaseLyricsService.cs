using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
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
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<NetEaseLyricsService> _logger;

    public NetEaseLyricsService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        ILogger<NetEaseLyricsService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://music.163.com/");
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _pipelines = pipelines;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> SearchLyricsAsync(string trackName, string? artistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return null;

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.NetEase))
        {
            _logger.LogDebug("NetEase circuit is open; skipping search.");
            return null;
        }

        try
        {
            var normalizedTrack = ArtistNameHelper.NormalizeStringCore(trackName) ?? trackName;
            var normalizedArtist = ArtistNameHelper.NormalizeStringCore(artistName) ?? artistName;

            var query = string.IsNullOrWhiteSpace(normalizedArtist)
                ? normalizedTrack
                : $"{normalizedTrack} {normalizedArtist}";

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
        var normalizedTrack = ArtistNameHelper.NormalizeStringCore(trackName) ?? trackName;
        var normalizedArtist = ArtistNameHelper.NormalizeStringCore(artistName) ?? artistName;

        return await _pipelines.ExecuteWithFallbackAsync<long?>(
            ServiceProviderIds.NetEase,
            async ct =>
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["s"] = query,
                    ["type"] = "1", // 1 = songs
                    ["limit"] = "10",
                    ["offset"] = "0"
                });
                _logger.LogDebug("Searching NetEase for: {Query}", query);
                return await _httpClient.PostAsync(SearchUrl, content, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("NetEase search failed with status {Status}.", response.StatusCode);
                    return null;
                }

                var result = await response.Content.ReadFromJsonAsync<NetEaseSearchResult>(cancellationToken: ct)
                    .ConfigureAwait(false);

                var songs = result?.Result?.Songs;
                if (songs is null || songs.Count == 0)
                    return null;

                // Score breakdown: TrackExact=4, TrackContains=2, ArtistMatch=1
                var bestMatch = songs
                    .Select(s =>
                    {
                        var normalizedSongName = ArtistNameHelper.NormalizeStringCore(s.Name) ?? s.Name;
                        var trackExact = normalizedSongName != null && normalizedSongName.Equals(normalizedTrack, StringComparison.OrdinalIgnoreCase);
                        var trackContains = normalizedSongName != null && normalizedSongName.Contains(normalizedTrack, StringComparison.OrdinalIgnoreCase);
                        var artistMatch = !string.IsNullOrWhiteSpace(normalizedArtist) &&
                                          s.Artists?.Any(a =>
                                          {
                                              var n = ArtistNameHelper.NormalizeStringCore(a.Name) ?? a.Name;
                                              return n != null && n.Contains(normalizedArtist, StringComparison.OrdinalIgnoreCase);
                                          }) == true;

                        if (!trackExact && !trackContains)
                            return (Song: (NetEaseSong?)null, Score: -1);

                        var score = (trackExact ? 4 : 0) + (trackContains ? 2 : 0) + (artistMatch ? 1 : 0);
                        return (Song: (NetEaseSong?)s, Score: score);
                    })
                    .Where(x => x.Song != null)
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                return bestMatch.Song?.Id;
            },
            fallback: null,
            _logger,
            $"search for {query}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetLyricsAsync(long songId, CancellationToken cancellationToken)
    {
        var url = $"{LyricsUrl}?id={songId}&lv=1";

        return await _pipelines.ExecuteWithFallbackAsync<string?>(
            ServiceProviderIds.NetEase,
            async ct =>
            {
                _logger.LogDebug("Fetching NetEase lyrics for song ID: {SongId}", songId);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("NetEase lyrics fetch failed with status {Status}.", response.StatusCode);
                    return null;
                }

                var result = await response.Content.ReadFromJsonAsync<NetEaseLyricsResult>(cancellationToken: ct)
                    .ConfigureAwait(false);

                var lrcContent = result?.Lrc?.Lyric;
                if (string.IsNullOrWhiteSpace(lrcContent))
                    return null;

                // Validate that it's actually LRC format (has timestamps).
                if (!LrcTimestampRegex().IsMatch(lrcContent))
                    return null;

                return lrcContent;
            },
            fallback: null,
            _logger,
            $"lyrics fetch for song ID {songId}",
            cancellationToken).ConfigureAwait(false);
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
