using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.OnlineProviders.Pipelines;

namespace Nagi.Core.OnlineProviders;

public class LastFmMetadataService : ILastFmMetadataService
{
    private const int NotFoundErrorCode = 6;
    private const int InvalidApiKeyErrorCode = 10;

    // Last.fm's placeholder star image, returned for artists without images.
    private const string PlaceholderImageHash = "2a96cbd8b46e442fc41c2b86b821562f";

    private readonly ProviderClient _client;
    private readonly ILogger<LastFmMetadataService> _logger;

    public LastFmMetadataService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ILogger<LastFmMetadataService> logger)
    {
        _client = new ProviderClient(ProviderCatalog.LastFm.Policy, httpClientFactory.CreateClient(), pipelines, logger,
            apiKeyService, ApiKeyNames.LastFm);
        _logger = logger;
    }

    public Task<ServiceResult<ArtistInfo>> GetArtistInfoAsync(string artistName,
        string? languageCode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(ServiceResult<ArtistInfo>.FromSuccessNotFound());

        var query = $"method=artist.getinfo&artist={Uri.EscapeDataString(artistName)}&autocorrect=1";
        // Last.fm takes ISO 639-1 codes; a language with only a three-letter code (Filipino's fil) gets English.
        if (languageCode is { Length: 2 })
            query += $"&lang={Uri.EscapeDataString(languageCode)}";

        return GetAsync<LastFmArtistResponse, ArtistInfo>(query,
            response => response.Artist is null ? null : ToArtistInfo(response.Artist),
            $"artist '{artistName}'", cancellationToken);
    }

    public Task<ServiceResult<string>> GetAlbumCoverUrlAsync(string albumTitle, string artistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(albumTitle) || string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(ServiceResult<string>.FromSuccessNotFound());

        return GetAsync<LastFmAlbumResponse, string>(
            $"method=album.getinfo&artist={Uri.EscapeDataString(artistName)}&album={Uri.EscapeDataString(albumTitle)}&autocorrect=1",
            // Autocorrect can land on a different album, so accept only the one that was asked for.
            response => response.Album is { } album && NameMatch.AreSameAlbum(album.Name, albumTitle) &&
                        NameMatch.AreEquivalent(album.Artist, artistName)
                ? SelectBestImageUrl(album.Image)
                : null,
            $"album '{albumTitle}' by '{artistName}'", cancellationToken);
    }

    private Task<ServiceResult<T>> GetAsync<TResponse, T>(string query, Func<TResponse, T?> map, string subject,
        CancellationToken cancellationToken)
        where TResponse : LastFmResponse
        where T : class =>
        _client.GetJsonAsync<TResponse, T>(
            apiKey => $"{LastFmApiHelper.BaseUrl}?{query}&api_key={apiKey}&format=json",
            response =>
            {
                var mapped = map(response);
                if (mapped is not null) _logger.LogInformation("Found Last.fm metadata for {Subject}", subject);
                return ServiceResult<T>.FromSuccessOrNotFound(mapped);
            },
            $"metadata for {subject}", cancellationToken,
            // Last.fm reports failures in the body, often alongside a 200, and an unknown artist or album as an error.
            response => response.ErrorCode switch
            {
                null => BodyStatus.Ok,
                NotFoundErrorCode => BodyStatus.NotFound,
                InvalidApiKeyErrorCode => BodyStatus.KeyRejected,
                _ => BodyStatus.Failed
            });

    private static ArtistInfo? ToArtistInfo(LastFmArtist artist)
    {
        var bio = SanitizeBiography(artist.Bio?.Summary);
        var imageUrl = SelectBestImageUrl(artist.Image);

        if (bio is null && imageUrl is null) return null;

        return new ArtistInfo
        {
            Biography = bio,
            ImageUrl = imageUrl
        };
    }

    // Drops the trailing "Read more on Last.fm" link.
    private static string? SanitizeBiography(string? rawBio)
    {
        if (string.IsNullOrWhiteSpace(rawBio)) return null;
        var linkIndex = rawBio.IndexOf("<a href=\"https://www.last.fm", StringComparison.Ordinal);
        return TextNormalizer.Normalize(linkIndex >= 0 ? rawBio[..linkIndex] : rawBio);
    }

    private static string? SelectBestImageUrl(IEnumerable<LastFmImage>? images)
    {
        if (images is null) return null;

        var imageList = images
            .Where(i => !string.IsNullOrWhiteSpace(i.Url) && !i.Url.Contains(PlaceholderImageHash, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return imageList.FirstOrDefault(i => i.Size == "mega")?.Url
               ?? imageList.FirstOrDefault(i => i.Size == "extralarge")?.Url
               ?? imageList.FirstOrDefault(i => i.Size == "large")?.Url
               ?? imageList.LastOrDefault()?.Url;
    }

    private abstract class LastFmResponse
    {
        [JsonPropertyName("error")] public int? ErrorCode { get; set; }
    }

    private sealed class LastFmArtistResponse : LastFmResponse
    {
        [JsonPropertyName("artist")] public LastFmArtist? Artist { get; set; }
    }

    private sealed class LastFmAlbumResponse : LastFmResponse
    {
        [JsonPropertyName("album")] public LastFmAlbum? Album { get; set; }
    }

    private sealed class LastFmAlbum
    {
        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("artist")] public string? Artist { get; set; }

        [JsonPropertyName("image")] public List<LastFmImage> Image { get; set; } = new();
    }

    private sealed class LastFmArtist
    {
        [JsonPropertyName("image")] public List<LastFmImage> Image { get; set; } = new();

        [JsonPropertyName("bio")] public LastFmBio? Bio { get; set; }
    }

    private sealed class LastFmImage
    {
        [JsonPropertyName("#text")] public string Url { get; set; } = string.Empty;

        [JsonPropertyName("size")] public string Size { get; set; } = string.Empty;
    }

    private sealed class LastFmBio
    {
        [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    }
}
