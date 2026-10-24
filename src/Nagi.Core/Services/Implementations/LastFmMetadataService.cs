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
    private const int NotFoundErrorCode = 6;
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

    public Task<ServiceResult<ArtistInfo>> GetArtistInfoAsync(string artistName,
        string? languageCode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(ServiceResult<ArtistInfo>.FromSuccessNotFound());

        var query = $"method=artist.getinfo&artist={Uri.EscapeDataString(artistName)}&autocorrect=1";
        // Last.fm expects ISO 639-1 (2-letter) language codes.
        if (!string.IsNullOrWhiteSpace(languageCode))
            query += $"&lang={Uri.EscapeDataString(languageCode.Length > 2 ? languageCode[..2] : languageCode)}";

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

    private async Task<ServiceResult<T>> GetAsync<TResponse, T>(string query, Func<TResponse, T?> map, string subject,
        CancellationToken cancellationToken)
        where TResponse : LastFmResponse
        where T : class
    {
        if (_pipelines.IsCircuitOpen(ServiceProviderIds.LastFm))
            return ServiceResult<T>.FromTemporaryError();

        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("Cannot fetch Last.fm metadata; API key '{ApiKeyName}' is unavailable.", ApiKeyName);
            return ServiceResult<T>.FromTemporaryError();
        }

        var (result, errorCode) = await TryFetchAsync(query, apiKey, map, subject, cancellationToken).ConfigureAwait(false);
        if (result is not null) return result;

        // Error code 10 = invalid API key. Refresh the key and retry once.
        if (errorCode == InvalidApiKeyErrorCode)
        {
            _logger.LogWarning("Last.fm API key invalid. Refreshing and retrying for {Subject}.", subject);
            apiKey = await _apiKeyService.RefreshApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("Failed to refresh Last.fm API key. Aborting fetch for {Subject}.", subject);
                return ServiceResult<T>.FromTemporaryError();
            }

            var (retryResult, _) = await TryFetchAsync(query, apiKey, map, subject, cancellationToken).ConfigureAwait(false);
            return retryResult ?? ServiceResult<T>.FromTemporaryError();
        }

        return ServiceResult<T>.FromTemporaryError();
    }

    private async Task<(ServiceResult<T>? Result, int? ErrorCode)> TryFetchAsync<TResponse, T>(
        string query, string apiKey, Func<TResponse, T?> map, string subject, CancellationToken cancellationToken)
        where TResponse : LastFmResponse
        where T : class
    {
        var requestUrl = $"{LastFmApiBaseUrl}?{query}&api_key={apiKey}&format=json";

        return await _pipelines.ExecuteWithFallbackAsync<(ServiceResult<T>? Result, int? ErrorCode)>(
            ServiceProviderIds.LastFm,
            async ct =>
            {
                _logger.LogDebug("Fetching Last.fm metadata for {Subject}", subject);
                return await _httpClient.GetAsync(requestUrl, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                var lastFmResponse = await response.Content
                    .ReadFromJsonAsync<TResponse>(cancellationToken: ct).ConfigureAwait(false);

                // Last.fm reports an unknown artist or album as an error rather than an empty result.
                if (lastFmResponse?.ErrorCode == NotFoundErrorCode)
                    return (ServiceResult<T>.FromSuccessNotFound(), null);

                if (!response.IsSuccessStatusCode || lastFmResponse?.ErrorCode is not null)
                {
                    _logger.LogWarning(
                        "Error fetching Last.fm metadata for {Subject}. Status: {StatusCode}, Error: {ErrorMessage}",
                        subject, response.StatusCode, lastFmResponse?.Message ?? "Unknown");
                    return ((ServiceResult<T>?)null, lastFmResponse?.ErrorCode);
                }

                var mapped = lastFmResponse is null ? null : map(lastFmResponse);
                if (mapped is null)
                    return (ServiceResult<T>.FromSuccessNotFound(), null);

                _logger.LogInformation("Found Last.fm metadata for {Subject}", subject);
                return (ServiceResult<T>.FromSuccess(mapped), null);
            },
            fallback: (ServiceResult<T>.FromTemporaryError(), null),
            _logger,
            $"metadata fetch for {subject}",
            cancellationToken).ConfigureAwait(false);
    }

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
}
