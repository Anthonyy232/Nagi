using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Searches Deezer's public catalog, which needs no API key. Its limit of 50 requests per 5 seconds
///     is enforced by <see cref="IProviderPipelineProvider" />.
/// </summary>
public class DeezerService : IDeezerService, IDisposable
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

    public void Dispose() => _httpClient.Dispose();

    public Task<ServiceResult<string>> GetArtistImageUrlAsync(string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(ServiceResult<string>.FromSuccessNotFound());

        // Deezer's quoted field search is accent-sensitive ("Beyonce" misses "Beyoncé"), so search loosely
        // and let the name check reject the near misses.
        return SearchAsync<DeezerArtist>(
            $"{BaseUrl}/search/artist?q={Uri.EscapeDataString(artistName)}&limit=10",
            artists => artists.FirstOrDefault(artist =>
                NameMatch.AreEquivalent(artist.Name, artistName) && HasPicture(artist.PictureXl))?.PictureXl,
            $"artist image for {artistName}",
            cancellationToken);
    }

    // Artists without a picture still get a URL, with the image hash left empty.
    private static bool HasPicture(string? url) =>
        !string.IsNullOrEmpty(url) && !url.Contains("/artist//", StringComparison.Ordinal);

    private async Task<ServiceResult<string>> SearchAsync<T>(
        string url, Func<List<T>, string?> selectUrl, string operation, CancellationToken cancellationToken)
    {
        if (_pipelines.IsCircuitOpen(ServiceProviderIds.Deezer))
            return ServiceResult<string>.FromTemporaryError();

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.Deezer,
            async ct =>
            {
                _logger.LogDebug("Searching Deezer for {Operation}", operation);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Deezer search failed with status {StatusCode} for {Operation}",
                        response.StatusCode, operation);
                    return ServiceResult<string>.FromTemporaryError();
                }

                var result = await response.Content.ReadFromJsonAsync<DeezerSearchResult<T>>(ct).ConfigureAwait(false);
                if (result?.Error is { } error)
                {
                    if (error.Code == DataNotFoundErrorCode) return ServiceResult<string>.FromSuccessNotFound();
                    _logger.LogWarning("Deezer returned error {Code} ({Message}) for {Operation}",
                        error.Code, error.Message, operation);
                    return ServiceResult<string>.FromTemporaryError();
                }

                var imageUrl = result?.Data is { } data ? selectUrl(data) : null;
                return imageUrl is null
                    ? ServiceResult<string>.FromSuccessNotFound()
                    : ServiceResult<string>.FromSuccess(imageUrl);
            },
            fallback: ServiceResult<string>.FromTemporaryError(),
            _logger,
            $"search for {operation}",
            cancellationToken).ConfigureAwait(false);
    }

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
}
