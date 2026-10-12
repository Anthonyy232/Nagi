using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public class LastFmMetadataService : ILastFmMetadataService
{
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private const int InvalidApiKeyErrorCode = 10;
    private const string ApiKeyName = ServiceProviderIds.LastFm;

    /// <summary>
    ///     The hash component of Last.fm's placeholder star image URL, returned for artists without images.
    /// </summary>
    private const string PlaceholderImageHash = "2a96cbd8b46e442fc41c2b86b821562f";

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

    public async Task<ServiceResult<ArtistInfo>> GetArtistInfoAsync(string artistName,
        string? languageCode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return ServiceResult<ArtistInfo>.FromSuccessNotFound();

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.LastFm))
            return ServiceResult<ArtistInfo>.FromTemporaryError();

        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("Cannot fetch Last.fm metadata; API key '{ApiKeyName}' is unavailable.", ApiKeyName);
            return ServiceResult<ArtistInfo>.FromTemporaryError();
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
                return ServiceResult<ArtistInfo>.FromTemporaryError();
            }

            var (retryResult, _) = await TryFetchAsync(artistName, apiKey, languageCode, cancellationToken)
                .ConfigureAwait(false);
            return retryResult ?? ServiceResult<ArtistInfo>.FromTemporaryError();
        }

        return ServiceResult<ArtistInfo>.FromTemporaryError();
    }

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
                var lastFmResponse = await response.Content
                    .ReadFromJsonAsync<LastFmArtistResponse>(cancellationToken: ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode || lastFmResponse?.ErrorCode is not null)
                {
                    _logger.LogWarning(
                        "Error fetching Last.fm metadata for '{ArtistName}'. Status: {StatusCode}, Error: {ErrorMessage}",
                        artistName, response.StatusCode, lastFmResponse?.Message ?? "Unknown");
                    return ((ServiceResult<ArtistInfo>?)null, lastFmResponse?.ErrorCode);
                }

                var artistInfo = lastFmResponse?.Artist != null ? ToArtistInfo(lastFmResponse.Artist) : null;
                if (artistInfo is null)
                    return (ServiceResult<ArtistInfo>.FromSuccessNotFound(), null);

                _logger.LogInformation(
                    "Found Last.fm metadata for {ArtistName} (Requested Language: {LanguageCode}): Bio={HasBio}, Image={HasImage}",
                    artistName, isoCode, !string.IsNullOrEmpty(artistInfo.Biography),
                    !string.IsNullOrEmpty(artistInfo.ImageUrl));
                return (ServiceResult<ArtistInfo>.FromSuccess(artistInfo), null);
            },
            fallback: (ServiceResult<ArtistInfo>.FromTemporaryError(), null),
            _logger,
            $"metadata fetch for {artistName}",
            cancellationToken).ConfigureAwait(false);
    }

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

    private static ArtistInfo? ToArtistInfo(Data.LastFmArtist artist)
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

    /// <summary>
    ///     Cleans the biography text, removing the trailing "Read more on Last.fm" link.
    /// </summary>
    private static string? SanitizeBiography(string? rawBio)
    {
        if (string.IsNullOrWhiteSpace(rawBio)) return null;
        var linkIndex = rawBio.IndexOf("<a href=\"https://www.last.fm", StringComparison.Ordinal);
        return ArtistNameHelper.NormalizeStringCore(linkIndex >= 0 ? rawBio[..linkIndex] : rawBio);
    }

    /// <summary>
    ///     Selects the most appropriate image URL from the available sizes, filtering out placeholder images.
    /// </summary>
    private static string? SelectBestImageUrl(IEnumerable<Data.LastFmImage>? images)
    {
        if (images is null) return null;

        var imageList = images
            .Where(i => !string.IsNullOrWhiteSpace(i.Url) && !i.Url.Contains(PlaceholderImageHash, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return imageList.FirstOrDefault(i => i.Size == "extralarge")?.Url
               ?? imageList.FirstOrDefault(i => i.Size == "large")?.Url
               ?? imageList.LastOrDefault()?.Url;
    }

}
