using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Presence;

/// <summary>
///     Looks up the cover art Cover Art Archive publishes for an album, so rich presence can
///     point Discord at a public https image instead of a local file.
/// </summary>
public sealed class AlbumArtUrlResolver : IAlbumArtUrlResolver
{
    private const string CoverArtArchiveUrl = "https://coverartarchive.org/release-group";

    // Discord renders the large image at roughly 300px; request a slightly larger thumbnail.
    private static readonly string[] ThumbnailSizes = ["500", "250", "1200"];

    private readonly ConcurrentDictionary<Guid, Lazy<Task<LookupResult>>> _cache = new();
    private readonly HttpClient _httpClient;
    private readonly ILogger<AlbumArtUrlResolver> _logger;
    private readonly IMusicBrainzService _musicBrainzService;
    private readonly IProviderPipelineProvider _pipelines;

    public AlbumArtUrlResolver(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        IMusicBrainzService musicBrainzService,
        ILogger<AlbumArtUrlResolver> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _musicBrainzService = musicBrainzService;
        _logger = logger;
    }

    public async Task<string?> GetArtworkUrlAsync(Song song, CancellationToken cancellationToken = default)
    {
        if (song.AlbumId is not { } albumId || song.Album is not { } album ||
            string.IsNullOrWhiteSpace(album.Title))
            return null;

        var lookup = _cache.GetOrAdd(albumId,
            _ => new Lazy<Task<LookupResult>>(() => LookupAsync(album.Title, album.ArtistName)));

        var result = await lookup.Value.WaitAsync(cancellationToken).ConfigureAwait(false);

        // A provider outage must not hide the artwork for the rest of the session.
        if (!result.IsConclusive)
            _cache.TryRemove(new KeyValuePair<Guid, Lazy<Task<LookupResult>>>(albumId, lookup));

        return result.Url;
    }

    private async Task<LookupResult> LookupAsync(string albumTitle, string artistName)
    {
        var releaseGroup = await _musicBrainzService
            .SearchReleaseGroupAsync(albumTitle, artistName).ConfigureAwait(false);

        if (!releaseGroup.IsConclusive) return LookupResult.Inconclusive;

        if (releaseGroup.Data is not { } releaseGroupId)
        {
            _logger.LogDebug("No MusicBrainz release group found for '{AlbumTitle}' by '{ArtistName}'.",
                albumTitle, artistName);
            return LookupResult.NotFound;
        }

        var result = await GetFrontCoverAsync(releaseGroupId).ConfigureAwait(false);
        if (result.Url is not null)
            _logger.LogDebug("Using Cover Art Archive artwork for '{AlbumTitle}': {Url}", albumTitle, result.Url);

        return result;
    }

    private Task<LookupResult> GetFrontCoverAsync(string releaseGroupId) =>
        _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.ImageDownload,
            async ct => await _httpClient.GetAsync($"{CoverArtArchiveUrl}/{releaseGroupId}", ct)
                .ConfigureAwait(false),
            async (response, ct) =>
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return LookupResult.NotFound;

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Cover Art Archive listing failed with status {StatusCode} for release group {ReleaseGroupId}.",
                        response.StatusCode, releaseGroupId);
                    return LookupResult.Inconclusive;
                }

                var listing = await response.Content.ReadFromJsonAsync<CoverArtArchiveListing>(
                    cancellationToken: ct).ConfigureAwait(false);
                return new LookupResult(SelectFrontCoverUrl(listing), true);
            },
            LookupResult.Inconclusive,
            _logger,
            $"cover art lookup for release group {releaseGroupId}").AsTask();

    private static string? SelectFrontCoverUrl(CoverArtArchiveListing? listing)
    {
        var images = listing?.Images;
        var frontCover = images?.FirstOrDefault(image => image.Front) ?? images?.FirstOrDefault();
        if (frontCover is null) return null;

        foreach (var size in ThumbnailSizes)
            if (frontCover.Thumbnails?.GetValueOrDefault(size) is { } thumbnail &&
                ToPublicImageUrl(thumbnail) is { } thumbnailUrl)
                return thumbnailUrl;

        return frontCover.Image is { } fullImage ? ToPublicImageUrl(fullImage) : null;
    }

    /// <summary>
    ///     Returns an https version of the image URL. Cover Art Archive publishes its listing
    ///     links over plain http, which Discord's image proxy cannot fetch; the https variant
    ///     redirects to the same archive.org image.
    /// </summary>
    private static string? ToPublicImageUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri.ToString();
        if (uri.Scheme != Uri.UriSchemeHttp) return null;

        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.ToString();
    }

    private readonly record struct LookupResult(string? Url, bool IsConclusive)
    {
        public static LookupResult NotFound => new(null, true);

        public static LookupResult Inconclusive => new(null, false);
    }

    private sealed class CoverArtArchiveListing
    {
        [JsonPropertyName("images")] public List<CoverArtImage>? Images { get; set; }
    }

    private sealed class CoverArtImage
    {
        [JsonPropertyName("front")] public bool Front { get; set; }

        [JsonPropertyName("image")] public string? Image { get; set; }

        [JsonPropertyName("thumbnails")] public Dictionary<string, string>? Thumbnails { get; set; }
    }
}
