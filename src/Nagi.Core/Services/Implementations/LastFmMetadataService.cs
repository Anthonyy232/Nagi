using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     A service for fetching artist information from the Last.fm API.
/// </summary>
public class LastFmMetadataService : ILastFmMetadataService
{
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private const int InvalidApiKeyErrorCode = 10;
    private const string ApiKeyName = ServiceProviderIds.LastFm;

    /// <summary>
    ///     The hash component of Last.fm's placeholder star image URL, returned for artists without images.
    /// </summary>
    private const string PlaceholderImageHash = "2a96cbd8b46e442fc41c2b86b821562f";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IApiKeyService _apiKeyService;
    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<LastFmMetadataService> _logger;

    public LastFmMetadataService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ILogger<LastFmMetadataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ServiceResult<ArtistInfo>> GetArtistInfoAsync(string artistName,
        string? languageCode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return ServiceResult<ArtistInfo>.FromSuccessNotFound();

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.LastFm))
            return ServiceResult<ArtistInfo>.FromTemporaryError("Provider is temporarily unavailable.");

        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("Cannot fetch Last.fm metadata; API key '{ApiKeyName}' is unavailable.", ApiKeyName);
            return ServiceResult<ArtistInfo>.FromPermanentError($"API key '{ApiKeyName}' is unavailable.");
        }

        var (result, errorCode) = await TryFetchAsync(artistName, apiKey, languageCode, cancellationToken)
            .ConfigureAwait(false);

        if (result is not null) return result;

        // Error code 10 = invalid API key. Refresh the key and retry once.
        if (errorCode == InvalidApiKeyErrorCode)
        {
            _logger.LogWarning("Last.fm API key invalid. Refreshing and retrying for '{ArtistName}'.", artistName);
            apiKey = await _apiKeyService.RefreshApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("Failed to refresh Last.fm API key. Aborting fetch for '{ArtistName}'.", artistName);
                return ServiceResult<ArtistInfo>.FromPermanentError("API key refresh failed.");
            }

            var (retryResult, _) = await TryFetchAsync(artistName, apiKey, languageCode, cancellationToken)
                .ConfigureAwait(false);
            return retryResult ?? ServiceResult<ArtistInfo>.FromTemporaryError("Failed after key refresh.");
        }

        return ServiceResult<ArtistInfo>.FromTemporaryError("Last.fm request failed.");
    }

    /// <summary>
    ///     One pipeline call. Returns a final result if one can be produced (success, not-found,
    ///     auth-disabled, or deserialization failure); otherwise returns the API error code so
    ///     the caller can decide whether to refresh credentials and retry.
    /// </summary>
    private async Task<(ServiceResult<ArtistInfo>? Result, int? ErrorCode)> TryFetchAsync(
        string artistName, string apiKey, string? languageCode, CancellationToken cancellationToken)
    {
        var requestUrl = BuildRequestUrl(artistName, apiKey, languageCode);
        var isoCode = languageCode?.Length > 2 ? languageCode[..2] : (languageCode ?? "Default");

        return await _pipelines.ExecuteWithFallbackAsync<(ServiceResult<ArtistInfo>? Result, int? ErrorCode)>(
            ServiceProviderIds.LastFm,
            async ct =>
            {
                _logger.LogDebug("Fetching Last.fm metadata for artist: {ArtistName} (Language: {LanguageCode})",
                    artistName, isoCode);
                return await _httpClient.GetAsync(requestUrl, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        var lastFmResponse = JsonSerializer.Deserialize<LastFmArtistResponse>(content, _jsonOptions);
                        var artistInfo = lastFmResponse?.Artist != null ? ToArtistInfo(lastFmResponse.Artist) : null;

                        if (artistInfo != null)
                        {
                            _logger.LogInformation(
                                "Found Last.fm metadata for {ArtistName} (Requested Language: {LanguageCode}): Bio={HasBio}, Image={HasImage}",
                                artistName, isoCode, !string.IsNullOrEmpty(artistInfo.Biography),
                                !string.IsNullOrEmpty(artistInfo.ImageUrl));
                            return (ServiceResult<ArtistInfo>.FromSuccess(artistInfo), null);
                        }

                        return (ServiceResult<ArtistInfo>.FromSuccessNotFound(), null);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Failed to deserialize Last.fm response for '{ArtistName}'.", artistName);
                        return (ServiceResult<ArtistInfo>.FromPermanentError(
                            $"Failed to deserialize Last.fm response for '{artistName}'."), null);
                    }
                }

                LastFmErrorResponse? errorResponse = null;
                if (!string.IsNullOrEmpty(content))
                {
                    try { errorResponse = JsonSerializer.Deserialize<LastFmErrorResponse>(content, _jsonOptions); }
                    catch (JsonException) { /* non-JSON error body — ignore */ }
                }

                var errorMessage = $"API call for '{artistName}' failed. Status: {response.StatusCode}, Error: {errorResponse?.Message ?? "Unknown"}";
                _logger.LogWarning("Error fetching Last.fm metadata: {ErrorMessage}", errorMessage);
                return ((ServiceResult<ArtistInfo>?)null, errorResponse?.ErrorCode);
            },
            fallback: (ServiceResult<ArtistInfo>.FromTemporaryError("Provider is temporarily unavailable."), null),
            _logger,
            $"metadata fetch for {artistName}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Constructs the full request URL for the Last.fm artist.getinfo method.
    /// </summary>
    private static string BuildRequestUrl(string artistName, string apiKey, string? languageCode)
    {
        var url = $"{LastFmApiBaseUrl}?method=artist.getinfo&artist={Uri.EscapeDataString(artistName)}&api_key={apiKey}&format=json&autocorrect=1";
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            // Last.fm expects ISO 639-1 (2-letter) language codes.
            var isoCode = languageCode.Length > 2 ? languageCode[..2] : languageCode;
            url += $"&lang={Uri.EscapeDataString(isoCode)}";
        }
        return url;
    }

    /// <summary>
    ///     Maps the Last.fm API response to the application's ArtistInfo DTO.
    /// </summary>
    private static ArtistInfo? ToArtistInfo(Data.LastFmArtist artist)
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
        return linkIndex > 0 ? ArtistNameHelper.NormalizeStringCore(rawBio[..linkIndex]) : ArtistNameHelper.NormalizeStringCore(rawBio);
    }

    /// <summary>
    ///     Selects the most appropriate image URL from the available sizes, filtering out placeholder images.
    /// </summary>
    private static string? SelectBestImageUrl(IEnumerable<Data.LastFmImage>? images)
    {
        if (images is null) return null;

        var imageList = images
            .Where(i => !string.IsNullOrEmpty(i.Url) && !IsPlaceholderImage(i.Url))
            .ToList();

        // Prefer larger images, but fall back to any available image.
        return imageList.FirstOrDefault(i => i.Size == "extralarge")?.Url
               ?? imageList.FirstOrDefault(i => i.Size == "large")?.Url
               ?? imageList.LastOrDefault()?.Url;
    }

    /// <summary>
    ///     Checks if the given URL points to Last.fm's placeholder star image.
    /// </summary>
    private static bool IsPlaceholderImage(string url)
        => url.Contains(PlaceholderImageHash, StringComparison.OrdinalIgnoreCase);
}
