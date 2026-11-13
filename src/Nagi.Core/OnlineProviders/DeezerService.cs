using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.OnlineProviders.Pipelines;

namespace Nagi.Core.OnlineProviders;

/// <summary>
///     Searches Deezer's public catalog, which needs no API key.
/// </summary>
public class DeezerService : IDeezerService
{
    private const string BaseUrl = "https://api.deezer.com";

    // Deezer reports errors in the body of a 200 response; this one means the search found nothing.
    private const int DataNotFoundErrorCode = 800;

    private readonly ProviderClient _client;

    public DeezerService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        ILogger<DeezerService> logger)
    {
        _client = new ProviderClient(ProviderCatalog.Deezer.Policy, httpClientFactory.CreateClient(), pipelines, logger);
    }

    public Task<ServiceResult<string>> GetArtistImageUrlAsync(string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(ServiceResult<string>.FromSuccessNotFound());

        // Deezer's quoted field search is accent-sensitive ("Beyonce" misses "Beyoncé"), so search loosely
        // and let the name check reject the near misses.
        return SearchAsync<DeezerArtist>(
            $"{BaseUrl}/search/artist?q={Uri.EscapeDataString(artistName)}&limit=10",
            artists => artists.FirstOrDefault(artist =>
                NameMatch.AreEquivalent(artist.Name, artistName) && HasImage(artist.PictureXl))?.PictureXl,
            $"artist image for {artistName}",
            cancellationToken);
    }

    public Task<ServiceResult<string>> GetAlbumCoverUrlAsync(string albumTitle, string artistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(albumTitle) || string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(ServiceResult<string>.FromSuccessNotFound());

        return SearchAsync<DeezerAlbum>(
            $"{BaseUrl}/search/album?q={Uri.EscapeDataString($"{artistName} {albumTitle}")}&limit=10",
            albums => albums.FirstOrDefault(album =>
                NameMatch.AreEquivalent(album.Artist?.Name, artistName) &&
                NameMatch.AreSameAlbum(album.Title, albumTitle) && HasImage(album.CoverXl))?.CoverXl,
            $"album cover for {albumTitle} by {artistName}",
            cancellationToken);
    }

    // Items without an image still get a URL, with the image hash left empty ("/images/cover//1000x1000-...").
    private static bool HasImage(string? url) =>
        !string.IsNullOrEmpty(url) &&
        url.IndexOf("//", url.IndexOf("://", StringComparison.Ordinal) + 3, StringComparison.Ordinal) < 0;

    private Task<ServiceResult<string>> SearchAsync<T>(
        string url, Func<List<T>, string?> selectUrl, string operation, CancellationToken cancellationToken) =>
        _client.GetJsonAsync<DeezerSearchResult<T>, string>(url,
            result => ServiceResult<string>.FromSuccessOrNotFound(result.Data is { } data ? selectUrl(data) : null),
            $"Deezer {operation}", cancellationToken,
            result => result.Error switch
            {
                null => BodyStatus.Ok,
                { Code: DataNotFoundErrorCode } => BodyStatus.NotFound,
                _ => BodyStatus.Failed
            });

    private sealed class DeezerSearchResult<T>
    {
        [JsonPropertyName("data")]
        public List<T>? Data { get; set; }

        [JsonPropertyName("error")]
        public DeezerError? Error { get; set; }
    }

    private sealed class DeezerError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }
    }

    private sealed class DeezerArtist
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("picture_xl")]
        public string? PictureXl { get; set; }
    }

    private sealed class DeezerAlbum
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("cover_xl")]
        public string? CoverXl { get; set; }

        [JsonPropertyName("artist")]
        public DeezerArtist? Artist { get; set; }
    }
}
