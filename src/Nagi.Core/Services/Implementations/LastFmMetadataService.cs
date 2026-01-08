using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http;
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
    private const int MaxRetries = 3;
    private const int InvalidApiKeyErrorCode = 10;
    private const string ApiKeyName = ServiceProviderIds.LastFm;

    /// <summary>
    ///     The hash component of Last.fm's placeholder star image URL, returned for artists without images.
    /// </summary>
    private const string PlaceholderImageHash = "2a96cbd8b46e442fc41c2b86b821562f";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IApiKeyService _apiKeyService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LastFmMetadataService> _logger;
    private volatile bool _isApiDisabled;

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
        if (string.IsNullOrWhiteSpace(artistName))
            return ServiceResult<ArtistInfo>.FromSuccessNotFound();

        if (_isApiDisabled)
            return ServiceResult<ArtistInfo>.FromPermanentError("Last.fm API is disabled for this session.");

        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("Cannot fetch Last.fm metadata; API key '{ApiKeyName}' is unavailable.", ApiKeyName);
            return ServiceResult<ArtistInfo>.FromPermanentError($"API key '{ApiKeyName}' is unavailable.");
        }

        var currentApiKey = apiKey;
        var operationName = $"Last.fm metadata fetch for {artistName}";

        var result = await HttpRetryHelper.ExecuteWithRetryAsync<ServiceResult<ArtistInfo>>(
            async attempt =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestUrl = BuildRequestUrl(artistName, currentApiKey);

                try
                {
                    _logger.LogDebug("Fetching Last.fm metadata for artist: {ArtistName} (Attempt {Attempt}/{MaxRetries})", 
                        artistName, attempt, MaxRetries);

                    using var response = await _httpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
                    var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        var lastFmResponse = JsonSerializer.Deserialize<LastFmArtistResponse>(content, _jsonOptions);
                        var artistInfo = lastFmResponse?.Artist != null ? ToArtistInfo(lastFmResponse.Artist) : null;

                        return artistInfo != null
                            ? RetryResult<ServiceResult<ArtistInfo>>.Success(ServiceResult<ArtistInfo>.FromSuccess(artistInfo))
                            : RetryResult<ServiceResult<ArtistInfo>>.Success(ServiceResult<ArtistInfo>.FromSuccessNotFound());
                    }

                    // Handle API-specific errors
                    LastFmErrorResponse? errorResponse = null;
                    if (!string.IsNullOrEmpty(content))
                        errorResponse = JsonSerializer.Deserialize<LastFmErrorResponse>(content, _jsonOptions);

                    if (errorResponse?.ErrorCode == InvalidApiKeyErrorCode && attempt < MaxRetries)
                    {
                        _logger.LogWarning(
                            "Last.fm API key is invalid. Refreshing and retrying request for artist '{ArtistName}'.",
                            artistName);
                        currentApiKey = await _apiKeyService.RefreshApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(currentApiKey))
                        {
                            _logger.LogError(
                                "Failed to refresh Last.fm API key. Aborting metadata fetch for '{ArtistName}'.",
                                artistName);
                            return RetryResult<ServiceResult<ArtistInfo>>.Success(
                                ServiceResult<ArtistInfo>.FromPermanentError("API key refresh failed."));
                        }
                        return RetryResult<ServiceResult<ArtistInfo>>.TransientFailure();
                    }

                    var errorMessage =
                        $"API call for '{artistName}' failed. Status: {response.StatusCode}, Error: {errorResponse?.Message ?? "Unknown"}";
                    
                    if (HttpRetryHelper.IsRetryableStatusCode(response.StatusCode))
                    {
                        _logger.LogWarning("Last.fm server error {StatusCode} for '{ArtistName}'. Retrying...", response.StatusCode, artistName);
                        return RetryResult<ServiceResult<ArtistInfo>>.TransientFailure();
                    }


                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _logger.LogError("Last.fm API access denied (401/403). Disabling for this session.");
                        _isApiDisabled = true;
                        return RetryResult<ServiceResult<ArtistInfo>>.Success(
                            ServiceResult<ArtistInfo>.FromPermanentError(errorMessage));
                    }

                    _logger.LogWarning("Error fetching Last.fm metadata: {ErrorMessage}", errorMessage);
                    return RetryResult<ServiceResult<ArtistInfo>>.Success(
                        ServiceResult<ArtistInfo>.FromTemporaryError(errorMessage));
                }
                catch (JsonException ex)
                {
                    var errorMessage = $"Failed to deserialize Last.fm response for '{artistName}'.";
                    _logger.LogError(ex, "{ErrorMessage}", errorMessage);
                    return RetryResult<ServiceResult<ArtistInfo>>.Success(
                        ServiceResult<ArtistInfo>.FromPermanentError(errorMessage));
                }
            },
            _logger,
            operationName,
            cancellationToken,
            MaxRetries
        ).ConfigureAwait(false);

        if (result != null)
            return result;

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
        return linkIndex > 0 ? rawBio[..linkIndex].Trim() : rawBio;
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