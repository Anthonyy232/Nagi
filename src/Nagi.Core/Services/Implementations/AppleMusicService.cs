using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Searches the Apple Music catalog through the public iTunes Search API, which needs no key.
/// </summary>
public class AppleMusicService : IAppleMusicService
{
    private const string SearchUrl = "https://itunes.apple.com/search";

    private readonly ProviderClient _client;

    public AppleMusicService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        ILogger<AppleMusicService> logger)
    {
        _client = new ProviderClient(ProviderCatalog.AppleMusic.Policy, httpClientFactory.CreateClient(), pipelines, logger);
    }

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

        return _client.GetJsonAsync<SearchResult, string>(url,
            result =>
            {
                var album = result.Results?.FirstOrDefault(candidate =>
                    NameMatch.AreEquivalent(candidate.ArtistName, artistName) &&
                    NameMatch.AreSameAlbum(candidate.CollectionName, albumTitle));

                // The size in the artwork URL is a template; asking for a larger one returns the original art.
                return ServiceResult<string>.FromSuccessOrNotFound(
                    album?.ArtworkUrl100?.Replace("100x100bb", "1200x1200bb", StringComparison.Ordinal));
            },
            $"Apple Music album search for {albumTitle} by {artistName}", cancellationToken);
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
