using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Searches the Apple Music catalog through the public iTunes Search API, which needs no key but allows
///     only about 20 requests a minute.
/// </summary>
public class AppleMusicService : IAppleMusicService, IDisposable
{
    private const string SearchUrl = "https://itunes.apple.com/search";

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<AppleMusicService> _logger;

    public AppleMusicService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        ILogger<AppleMusicService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _logger = logger;
    }

    public void Dispose() => _httpClient.Dispose();

    public Task<ServiceResult<string>> GetAlbumCoverUrlAsync(string albumTitle, string artistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(albumTitle) || string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(ServiceResult<string>.FromSuccessNotFound());

        // Each storefront has its own catalog, so search the one the user is most likely to know. The invariant
        // region reports "IV", which is no storefront.
        var country = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        var url = $"{SearchUrl}?term={Uri.EscapeDataString($"{artistName} {albumTitle}")}&entity=album&limit=25" +
                  (country.Length == 2 && country != "IV" ? $"&country={country}" : string.Empty);

        return _pipelines.GetJsonAsync<SearchResult, string>(_httpClient, ServiceProviderIds.AppleMusic, url,
            result =>
            {
                var album = result.Results?.FirstOrDefault(candidate =>
                    NameMatch.AreEquivalent(candidate.ArtistName, artistName) &&
                    NameMatch.AreSameAlbum(candidate.CollectionName, albumTitle));

                // The size in the artwork URL is a template; asking for a larger one returns the original art.
                return ServiceResult<string>.FromSuccessOrNotFound(
                    album?.ArtworkUrl100?.Replace("100x100bb", "1200x1200bb", StringComparison.Ordinal));
            },
            _logger, $"Apple Music album search for {albumTitle} by {artistName}", cancellationToken);
    }

    private sealed class SearchResult
    {
        [JsonPropertyName("results")]
        public List<AlbumResult>? Results { get; set; }
    }

    private sealed class AlbumResult
    {
        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("collectionName")]
        public string? CollectionName { get; set; }

        [JsonPropertyName("artworkUrl100")]
        public string? ArtworkUrl100 { get; set; }
    }
}
