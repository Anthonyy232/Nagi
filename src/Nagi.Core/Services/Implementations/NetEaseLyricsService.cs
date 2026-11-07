using ModernLrc;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Finds synchronized lyrics on NetEase Cloud Music through the unofficial endpoints open-source players use. It
///     often has lyrics for Asian music (J-Pop, K-Pop, anime) that LRCLIB lacks.
/// </summary>
public class NetEaseLyricsService : ILyricsProvider
{
    private const string SearchUrl = "https://music.163.com/api/search/get";
    private const string LyricsUrl = "https://music.163.com/api/song/lyric";

    private readonly ProviderClient _client;
    private readonly ILogger<NetEaseLyricsService> _logger;

    public NetEaseLyricsService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        ILogger<NetEaseLyricsService> logger)
    {
        _client = new ProviderClient(ProviderCatalog.NetEase.Policy, httpClientFactory.CreateClient(), pipelines, logger);
        _client.DefaultRequestHeaders.Add("Referer", "https://music.163.com/");
        // The web API expects a browser, so this replaces the Nagi user agent every client gets by default.
        _client.DefaultRequestHeaders.UserAgent.Clear();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _logger = logger;
    }

    public string Id => ServiceProviderIds.NetEase;

    public async Task<ServiceResult<string>> GetLyricsAsync(Song song, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(song.Title))
            return ServiceResult<string>.FromSuccessNotFound();

        var trackName = TextNormalizer.Normalize(song.Title)!;
        var artistName = TextNormalizer.Normalize(song.PrimaryArtistName);
        if (string.Equals(artistName, Artist.UnknownArtistName, StringComparison.OrdinalIgnoreCase)) artistName = null;

        var query = artistName is null ? trackName : $"{trackName} {artistName}";
        var match = await _client.SendJsonAsync<NetEaseSearchResult, NetEaseSong>(_ =>
                new HttpRequestMessage(HttpMethod.Post, SearchUrl)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["s"] = query,
                        ["type"] = "1", // 1 = songs
                        ["limit"] = "10",
                        ["offset"] = "0"
                    })
                },
                result => ServiceResult<NetEaseSong>.FromSuccessOrNotFound(SelectBestMatch(result, trackName, artistName)),
                $"song search for {query}", cancellationToken)
            .ConfigureAwait(false);
        if (match.Data is null)
        {
            if (match.IsConclusive) _logger.LogDebug("No NetEase match found for: {Query}", query);
            return match.IsConclusive ? ServiceResult<string>.FromSuccessNotFound() : ServiceResult<string>.FromTemporaryError();
        }

        var lyrics = await _client.GetJsonAsync<NetEaseLyricsResult, string>($"{LyricsUrl}?id={match.Data.Id}&lv=1",
                result => ServiceResult<string>.FromSuccessOrNotFound(result.Lrc?.Lyric is { } lrc &&
                                                                        !string.IsNullOrWhiteSpace(lrc) &&
                                                                        LrcParser.Parse(lrc).Document.Lines.Count > 0
                    ? lrc
                    : null),
                $"lyrics for song {match.Data.Id}", cancellationToken)
            .ConfigureAwait(false);
        if (lyrics.Data is not null) _logger.LogInformation("Found NetEase lyrics for: {TrackName}", song.Title);
        return lyrics;
    }

    // Score: exact title 4, title contained 2, plus 1 for a matching artist.
    private static NetEaseSong? SelectBestMatch(NetEaseSearchResult result, string trackName, string? artistName) =>
        result.Result?.Songs?
            .Select(s =>
            {
                var normalizedSongName = TextNormalizer.Normalize(s.Name);
                var trackScore = string.Equals(normalizedSongName, trackName, StringComparison.OrdinalIgnoreCase) ? 4
                    : normalizedSongName?.Contains(trackName, StringComparison.OrdinalIgnoreCase) == true ? 2 : 0;
                var artistMatch = artistName is not null &&
                                  s.Artists?.Any(a =>
                                  {
                                      var name = TextNormalizer.Normalize(a.Name);
                                      return name?.Contains(artistName, StringComparison.OrdinalIgnoreCase) == true;
                                  }) == true;

                return (Song: s, Score: trackScore == 0 ? 0 : trackScore + (artistMatch ? 1 : 0));
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Song)
            .FirstOrDefault();

    private sealed class NetEaseSearchResult
    {
        public NetEaseSearchResultData? Result { get; set; }
    }

    private sealed class NetEaseSearchResultData
    {
        public List<NetEaseSong>? Songs { get; set; }
    }

    private sealed class NetEaseSong
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public List<NetEaseArtist>? Artists { get; set; }
    }

    private sealed class NetEaseArtist
    {
        public string? Name { get; set; }
    }

    private sealed class NetEaseLyricsResult
    {
        public NetEaseLyric? Lrc { get; set; }
    }

    private sealed class NetEaseLyric
    {
        public string? Lyric { get; set; }
    }
}
