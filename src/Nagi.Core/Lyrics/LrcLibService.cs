using System.Globalization;
using System.Web;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.OnlineProviders;
using Nagi.Core.OnlineProviders.Pipelines;
using Nagi.Core.Platform;

namespace Nagi.Core.Lyrics;

/// <summary>
///     Looks lyrics up on LRCLIB (https://lrclib.net/docs), preferring synchronized lyrics over plain text.
/// </summary>
public class LrcLibService : ILyricsProvider
{
    private const string BaseUrl = "https://lrclib.net/api/get";
    private const string SearchUrl = "https://lrclib.net/api/search";

    private readonly ProviderClient _client;
    private readonly ILogger<LrcLibService> _logger;

    public LrcLibService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        ILogger<LrcLibService> logger)
    {
        _client = new ProviderClient(ProviderCatalog.LrcLib.Policy, httpClientFactory.CreateClient(), pipelines, logger);
        _logger = logger;
    }

    public string Id => ServiceProviderIds.LrcLib;

    public async Task<ServiceResult<string>> GetLyricsAsync(Song song, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(song.Title))
            return ServiceResult<string>.FromSuccessNotFound();

        var trackName = TextNormalizer.Normalize(song.Title)!;
        var artistName = TextNormalizer.Normalize(song.PrimaryArtistName);
        var albumName = TextNormalizer.Normalize(song.Album?.Title);
        if (string.Equals(artistName, Artist.UnknownArtistName, StringComparison.OrdinalIgnoreCase)) artistName = null;
        if (string.Equals(albumName, Album.UnknownAlbumName, StringComparison.OrdinalIgnoreCase)) albumName = null;

        LrcLibResponse? strictResult = null;
        if (artistName is not null && albumName is not null)
        {
            var strict = await GetStrictMatchAsync(trackName, artistName, albumName, song.Duration, cancellationToken)
                .ConfigureAwait(false);
            if (!strict.IsConclusive) return ServiceResult<string>.FromTemporaryError();
            strictResult = strict.Data;
            if (!string.IsNullOrWhiteSpace(strictResult?.SyncedLyrics))
                return ServiceResult<string>.FromSuccess(strictResult.SyncedLyrics);
        }
        else
        {
            _logger.LogDebug("Strict lookup skipped (missing artist or album). Using search for: {Track}", trackName);
        }

        var search = await SearchAsync(trackName, artistName, albumName, song.Duration, cancellationToken)
            .ConfigureAwait(false);
        if (!search.IsConclusive) return ServiceResult<string>.FromTemporaryError();

        var searchResult = search.Data;
        if (!string.IsNullOrWhiteSpace(searchResult?.SyncedLyrics))
            return ServiceResult<string>.FromSuccess(searchResult.SyncedLyrics);
        return ServiceResult<string>.FromSuccessOrNotFound(
            !string.IsNullOrWhiteSpace(strictResult?.PlainLyrics) ? strictResult.PlainLyrics : searchResult?.PlainLyrics);
    }

    private Task<ServiceResult<LrcLibResponse>> GetStrictMatchAsync(string trackName, string artistName,
        string albumName, TimeSpan duration, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["track_name"] = trackName;
        query["artist_name"] = artistName;
        query["album_name"] = albumName;
        query["duration"] = duration.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture);

        return _client.GetJsonAsync<LrcLibResponse, LrcLibResponse>($"{BaseUrl}?{query}",
            ServiceResult<LrcLibResponse>.FromSuccess, $"lyrics for {artistName} - {trackName}", cancellationToken);
    }

    private Task<ServiceResult<LrcLibResponse>> SearchAsync(string trackName, string? artistName, string? albumName,
        TimeSpan duration, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["track_name"] = trackName;
        if (artistName is not null) query["artist_name"] = artistName;
        // Not sending album_name to search to be more permissive; filter locally below.

        return _client.GetJsonAsync<List<LrcLibResponse>, LrcLibResponse>($"{SearchUrl}?{query}", results =>
        {
            // Prefer synchronized lyrics, then album match and closest duration within ±30s.
            var targetDurationSeconds = duration.TotalSeconds;
            var bestMatch = results
                .Where(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics) || !string.IsNullOrWhiteSpace(r.PlainLyrics))
                .Where(r => Math.Abs(r.Duration - targetDurationSeconds) <= 30)
                .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
                .ThenByDescending(r => albumName is not null && string.Equals(r.AlbumName, albumName, StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => Math.Abs(r.Duration - targetDurationSeconds))
                .FirstOrDefault();

            if (bestMatch is null)
                _logger.LogDebug("No LRCLIB search result matched {Artist} - {Track}", artistName, trackName);
            return ServiceResult<LrcLibResponse>.FromSuccessOrNotFound(bestMatch);
        }, $"lyrics search for {artistName} - {trackName}", cancellationToken);
    }

    private sealed class LrcLibResponse
    {
        public string? AlbumName { get; set; }
        public double Duration { get; set; }
        public string? SyncedLyrics { get; set; }
        public string? PlainLyrics { get; set; }
    }
}
