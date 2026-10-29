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
///     Searches Deezer's public catalog, which needs no API key. Its limit of 50 requests per 5 seconds
///     is enforced by <see cref="IProviderPipelineProvider" />.
/// </summary>
public class DeezerService : IDeezerService
{
    private const string BaseUrl = "https://api.deezer.com";

    // Deezer reports errors in a 200 response; this one means the search found nothing.
    private const int DataNotFoundErrorCode = 800;

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<DeezerService> _logger;

    public DeezerService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        ILogger<DeezerService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _logger = logger;
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
        _pipelines.GetJsonAsync<DeezerSearchResult<T>, string>(_httpClient, ServiceProviderIds.Deezer, url,
            result =>
            {
                if (result.Error is { } error)
                {
                    if (error.Code == DataNotFoundErrorCode) return ServiceResult<string>.FromSuccessNotFound();
                    _logger.LogWarning("Deezer returned error {Code} ({Message}) for {Operation}",
                        error.Code, error.Message, operation);
                    return ServiceResult<string>.FromTemporaryError();
                }

                return ServiceResult<string>.FromSuccessOrNotFound(result.Data is { } data ? selectUrl(data) : null);
            },
            _logger, $"Deezer {operation}", cancellationToken);

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

        [JsonPropertyName("message")]
        public string? Message { get; set; }
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
