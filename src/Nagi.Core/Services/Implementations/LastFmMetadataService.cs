using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     A service for fetching artist information from the Last.fm API.
/// </summary>
public class LastFmMetadataService : ILastFmMetadataService
{
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private const int MaxRetries = 1;
    private const int InvalidApiKeyErrorCode = 10;
    private const string ApiKeyName = "lastfm";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IApiKeyService _apiKeyService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LastFmMetadataService> _logger;

    public LastFmMetadataService(IHttpClientFactory httpClientFactory, IApiKeyService apiKeyService,
        ILogger<LastFmMetadataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ServiceResult<ArtistInfo>> GetArtistInfoAsync(string artistName,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("Cannot fetch Last.fm metadata; API key '{ApiKeyName}' is unavailable.", ApiKeyName);
            return ServiceResult<ArtistInfo>.FromPermanentError($"API key '{ApiKeyName}' is unavailable.");
        }

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestUrl = BuildRequestUrl(artistName, apiKey);

            try
            {
                using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);

                // Copy to a memory stream to allow for re-reading (e.g., for error parsing).
                await using var contentStream = new MemoryStream();
                await response.Content.CopyToAsync(contentStream, cancellationToken);
                contentStream.Position = 0;

                if (response.IsSuccessStatusCode)
                {
                    var lastFmResponse =
                        await JsonSerializer.DeserializeAsync<LastFmArtistResponse>(contentStream, _jsonOptions,
                            cancellationToken);
                    var artistInfo = lastFmResponse?.Artist != null ? ToArtistInfo(lastFmResponse.Artist) : null;

                    return artistInfo != null
                        ? ServiceResult<ArtistInfo>.FromSuccess(artistInfo)
                        : ServiceResult<ArtistInfo>.FromSuccessNotFound();
                }

                // Handle API-specific errors
                LastFmErrorResponse? errorResponse = null;
                if (contentStream.Length > 0)
                    errorResponse =
                        await JsonSerializer.DeserializeAsync<LastFmErrorResponse>(contentStream, _jsonOptions,
                            cancellationToken);

                if (errorResponse?.ErrorCode == InvalidApiKeyErrorCode && attempt < MaxRetries)
                {
                    _logger.LogWarning(
                        "Last.fm API key is invalid. Refreshing and retrying request for artist '{ArtistName}'.",
                        artistName);
                    apiKey = await _apiKeyService.RefreshApiKeyAsync(ApiKeyName, cancellationToken);
                    if (string.IsNullOrEmpty(apiKey))
                    {
                        _logger.LogError(
                            "Failed to refresh Last.fm API key. Aborting metadata fetch for '{ArtistName}'.",
                            artistName);
                        return ServiceResult<ArtistInfo>.FromPermanentError("API key refresh failed.");
                    }

                    continue; // Retry the loop with the new key.
                }

                var errorMessage =
                    $"API call for '{artistName}' failed. Status: {response.StatusCode}, Error: {errorResponse?.Message ?? "Unknown"}";
                _logger.LogWarning("Temporary error fetching Last.fm metadata: {ErrorMessage}", errorMessage);
                return ServiceResult<ArtistInfo>.FromTemporaryError(errorMessage);
            }
            catch (JsonException ex)
            {
                var errorMessage = $"Failed to deserialize Last.fm response for '{artistName}'.";
                _logger.LogError(ex, "{ErrorMessage}", errorMessage);
                return ServiceResult<ArtistInfo>.FromPermanentError(errorMessage);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var errorMessage = $"Exception during API call for '{artistName}'.";
                _logger.LogWarning(ex, "{ErrorMessage}", errorMessage);
                return ServiceResult<ArtistInfo>.FromTemporaryError(errorMessage);
            }
        }

        _logger.LogWarning("Max retries exceeded for Last.fm request for artist '{ArtistName}'.", artistName);
        return ServiceResult<ArtistInfo>.FromTemporaryError("Max retries exceeded for Last.fm request.");
    }

    /// <summary>
    ///     Constructs the full request URL for the Last.fm artist.getinfo method.
    /// </summary>
    private static string BuildRequestUrl(string artistName, string apiKey)
    {
        return
            $"{LastFmApiBaseUrl}?method=artist.getinfo&artist={Uri.EscapeDataString(artistName)}&api_key={apiKey}&format=json";
    }

    /// <summary>
    ///     Maps the Last.fm API response to the application's ArtistInfo DTO.
    /// </summary>
    private static ArtistInfo? ToArtistInfo(LastFmArtist artist)
    {
        var bio = SanitizeBiography(artist.Bio?.Summary);
        var imageUrl = SelectBestImageUrl(artist.Image);

        // Only return an object if there is at least some information to display.
        if (bio is null && imageUrl is null) return null;

        return new ArtistInfo
        {
            Biography = bio,
            ImageUrl = imageUrl
        };
    }

    /// <summary>
    ///     Cleans the biography text, removing the trailing "Read more on Last.fm" link.
    /// </summary>
    private static string? SanitizeBiography(string? rawBio)
    {
        if (string.IsNullOrWhiteSpace(rawBio)) return null;
        var linkIndex = rawBio.IndexOf("<a href=\"https://www.last.fm", StringComparison.Ordinal);
        return linkIndex > 0 ? rawBio[..linkIndex].Trim() : rawBio;
    }

    /// <summary>
    ///     Selects the most appropriate image URL from the available sizes.
    /// </summary>
    private static string? SelectBestImageUrl(IEnumerable<LastFmImage>? images)
    {
        if (images is null) return null;
        var imageList = images.ToList();

        // Prefer larger images, but fall back to any available image.
        return imageList.FirstOrDefault(i => i.Size == "extralarge" && !string.IsNullOrEmpty(i.Url))?.Url
               ?? imageList.FirstOrDefault(i => i.Size == "large" && !string.IsNullOrEmpty(i.Url))?.Url
               ?? imageList.LastOrDefault(i => !string.IsNullOrEmpty(i.Url))?.Url;
    }

    // Private DTOs for deserializing the Last.fm API response.
    // Encapsulating them here prevents polluting the global namespace.
    private class LastFmArtistResponse
    {
        public LastFmArtist? Artist { get; set; }
    }

    private class LastFmArtist
    {
        public LastFmBio? Bio { get; set; }
        public IEnumerable<LastFmImage>? Image { get; set; }
    }

    private class LastFmBio
    {
        public string? Summary { get; set; }
    }


    private class LastFmImage
    {
        [JsonPropertyName("#text")] public string? Url { get; set; }
        public string? Size { get; set; }
    }

    private class LastFmErrorResponse
    {
        [JsonPropertyName("error")] public int ErrorCode { get; set; }
        public string? Message { get; set; }
    }
}