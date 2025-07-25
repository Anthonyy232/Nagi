using System.Diagnostics;
using System.Text.Json;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
/// A service for fetching artist information from the Last.fm API.
/// </summary>
public class LastFmMetadataService : ILastFmMetadataService {
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private const int MaxRetries = 1;
    private const int InvalidApiKeyErrorCode = 10;
    private const string ApiKeyName = "lastfm";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IApiKeyService _apiKeyService;
    private readonly HttpClient _httpClient;

    public LastFmMetadataService(IHttpClientFactory httpClientFactory, IApiKeyService apiKeyService) {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
    }

    /// <inheritdoc />
    public async Task<ArtistInfo?> GetArtistInfoAsync(string artistName, CancellationToken cancellationToken = default) {
        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken);
        if (string.IsNullOrEmpty(apiKey)) {
            Debug.WriteLine($"[LastFmService] Cannot get artist info; API key '{ApiKeyName}' is unavailable.");
            return null;
        }

        for (var attempt = 0; attempt <= MaxRetries; attempt++) {
            cancellationToken.ThrowIfCancellationRequested();

            var requestUrl = BuildRequestUrl(artistName, apiKey);
            try {
                using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);

                // Use a seekable stream for potential re-reads (e.g., for error parsing).
                await using var memoryStream = new MemoryStream();
                await response.Content.CopyToAsync(memoryStream, cancellationToken);
                memoryStream.Position = 0;

                if (response.IsSuccessStatusCode) {
                    var lastFmResponse = await JsonSerializer.DeserializeAsync<LastFmArtistResponse>(memoryStream, _jsonOptions, cancellationToken);
                    return lastFmResponse?.Artist != null ? ToArtistInfo(lastFmResponse.Artist) : null;
                }

                // If the request failed, attempt to parse an error response.
                var errorResponse = await JsonSerializer.DeserializeAsync<LastFmErrorResponse>(memoryStream, _jsonOptions, cancellationToken);
                if (errorResponse?.ErrorCode == InvalidApiKeyErrorCode && attempt < MaxRetries) {
                    Debug.WriteLine($"[LastFmService] Invalid Last.fm API key. Refreshing and retrying.");
                    apiKey = await _apiKeyService.RefreshApiKeyAsync(ApiKeyName, cancellationToken);
                    if (string.IsNullOrEmpty(apiKey)) return null; // Abort if refresh fails.
                    continue; // Retry with the new key.
                }

                Debug.WriteLine($"[LastFmService] API call for '{artistName}' failed. Status: {response.StatusCode}, Error: {errorResponse?.Message ?? "Unknown"}");
                return null;
            }
            catch (JsonException ex) {
                Debug.WriteLine($"[LastFmService] Failed to deserialize Last.fm response for '{artistName}': {ex.Message}");
                return null; // A deserialization error is not recoverable.
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the fully-qualified URL for the artist.getinfo API method.
    /// </summary>
    private static string BuildRequestUrl(string artistName, string apiKey) {
        return $"{LastFmApiBaseUrl}?method=artist.getinfo&artist={Uri.EscapeDataString(artistName)}&api_key={apiKey}&format=json";
    }

    /// <summary>
    /// Maps the raw Last.fm API artist data to the application's ArtistInfo model.
    /// </summary>
    private static ArtistInfo ToArtistInfo(LastFmArtist artist) {
        return new ArtistInfo {
            Biography = SanitizeBiography(artist.Bio?.Summary),
            ImageUrl = SelectBestImageUrl(artist.Image)
        };
    }

    /// <summary>
    /// Cleans the biography text, removing the "Read more on Last.fm" link.
    /// </summary>
    private static string SanitizeBiography(string? rawBio) {
        if (string.IsNullOrWhiteSpace(rawBio)) {
            return "No biography available for this artist.";
        }

        var linkIndex = rawBio.IndexOf("<a href=\"https://www.last.fm", StringComparison.Ordinal);
        return linkIndex > 0 ? rawBio.Substring(0, linkIndex).Trim() : rawBio;
    }

    /// <summary>
    /// Selects the best available image URL from the list, preferring larger sizes.
    /// </summary>
    private static string? SelectBestImageUrl(IEnumerable<LastFmImage>? images) {
        if (images == null) return null;

        var imageList = images.ToList();
        return imageList.FirstOrDefault(i => i.Size == "extralarge" && !string.IsNullOrEmpty(i.Url))?.Url
            ?? imageList.FirstOrDefault(i => i.Size == "large" && !string.IsNullOrEmpty(i.Url))?.Url
            ?? imageList.LastOrDefault(i => !string.IsNullOrEmpty(i.Url))?.Url;
    }
}