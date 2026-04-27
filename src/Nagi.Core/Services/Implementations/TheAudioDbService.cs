using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

// File-local DTOs for deserializing TheAudioDB API response.
file class TheAudioDbResponse
{
    [JsonPropertyName("artists")]
    public List<TheAudioDbArtist>? Artists { get; set; }
}

file class TheAudioDbArtist
{
    [JsonPropertyName("strBiographyEN")]
    public string? BiographyEN { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonPropertyName("strArtistThumb")]
    public string? ArtistThumb { get; set; }

    [JsonPropertyName("strArtistFanart")]
    public string? ArtistFanart { get; set; }

    [JsonPropertyName("strArtistFanart2")]
    public string? ArtistFanart2 { get; set; }

    [JsonPropertyName("strArtistFanart3")]
    public string? ArtistFanart3 { get; set; }

    [JsonPropertyName("strArtistWideThumb")]
    public string? ArtistWideThumb { get; set; }

    [JsonPropertyName("strArtistLogo")]
    public string? ArtistLogo { get; set; }
}

/// <summary>
///     Service for fetching artist metadata from TheAudioDB. Rate limiting (free tier
///     is 30 req/min), retry, and circuit breaker are handled by
///     <see cref="IProviderPipelineProvider"/>.
/// </summary>
public class TheAudioDbService : ITheAudioDbService, IDisposable
{
    private const string BaseUrl = "https://www.theaudiodb.com/api/v1/json";
    private const string ApiKeyName = ServiceProviderIds.TheAudioDb;

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<TheAudioDbService> _logger;

    private bool _isDisposed;

    public TheAudioDbService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ILogger<TheAudioDbService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _httpClient.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<ServiceResult<TheAudioDbArtistInfo>> GetArtistMetadataAsync(
        string musicBrainzId,
        string? languageCode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(musicBrainzId))
            return ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound();

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.TheAudioDb))
            return ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError("Provider is temporarily unavailable.");

        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("TheAudioDB API key not available.");
            return ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError("API key not available.");
        }

        var url = $"{BaseUrl}/{apiKey}/artist-mb.php?i={musicBrainzId}";
        var isoCode = languageCode?.Length > 2 ? languageCode[..2] : (languageCode ?? "Default");

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.TheAudioDb,
            async ct =>
            {
                _logger.LogDebug("Fetching TheAudioDB metadata for MBID: {MBID} (Language: {LanguageCode})",
                    musicBrainzId, isoCode);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("No TheAudioDB data found for MBID: {MBID}", musicBrainzId);
                    return ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound();
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TheAudioDB request failed with status {StatusCode} for MBID: {MBID}",
                        response.StatusCode, musicBrainzId);
                    return ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError($"HTTP {response.StatusCode}");
                }

                var apiResult = await response.Content.ReadFromJsonAsync<TheAudioDbResponse>(cancellationToken: ct)
                    .ConfigureAwait(false);

                var artist = apiResult?.Artists?.FirstOrDefault();
                if (artist is null)
                {
                    _logger.LogDebug("TheAudioDB returned null/empty artist data for MBID: {MBID}", musicBrainzId);
                    return ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound();
                }

                var fanartUrl = GetFirstNonEmpty(artist.ArtistFanart, artist.ArtistFanart2, artist.ArtistFanart3);

                var bio = GetLocalizedBiography(artist.ExtensionData, languageCode);
                var isLocalizedBio = !string.IsNullOrEmpty(bio);
                bio ??= artist.BiographyEN;

                var info = new TheAudioDbArtistInfo(
                    Biography: SanitizeBiography(bio),
                    ThumbUrl: NullIfEmpty(artist.ArtistThumb),
                    FanartUrl: fanartUrl,
                    WideThumbUrl: NullIfEmpty(artist.ArtistWideThumb),
                    LogoUrl: NullIfEmpty(artist.ArtistLogo)
                );

                if (info.Biography is null && info.ThumbUrl is null && info.FanartUrl is null &&
                    info.WideThumbUrl is null && info.LogoUrl is null)
                {
                    _logger.LogDebug("TheAudioDB returned empty metadata for MBID: {MBID}", musicBrainzId);
                    return ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound();
                }

                _logger.LogInformation("Found TheAudioDB metadata for MBID: {MBID} (Language: {LanguageCode}): Bio={HasBio} ({BioSource}), Thumb={HasThumb}, Fanart={HasFanart}, WideThumb={HasWideThumb}, Logo={HasLogo}",
                    musicBrainzId, isoCode, !string.IsNullOrEmpty(info.Biography), isLocalizedBio ? isoCode : "en",
                    !string.IsNullOrEmpty(info.ThumbUrl), !string.IsNullOrEmpty(info.FanartUrl),
                    !string.IsNullOrEmpty(info.WideThumbUrl), !string.IsNullOrEmpty(info.LogoUrl));
                return ServiceResult<TheAudioDbArtistInfo>.FromSuccess(info);
            },
            fallback: ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError("Provider is temporarily unavailable."),
            _logger,
            $"fetch metadata for MBID {musicBrainzId}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Cleans the biography text by trimming and returning null for empty/whitespace strings.
    /// </summary>
    private static string? SanitizeBiography(string? bio)
    {
        if (string.IsNullOrWhiteSpace(bio)) return null;
        return ArtistNameHelper.NormalizeStringCore(bio);
    }

    /// <summary>
    ///     Returns the first non-null, non-empty string from the provided values.
    /// </summary>
    private static string? GetFirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    /// <summary>
    ///     Returns null if the string is null or whitespace, otherwise returns the trimmed string.
    /// </summary>
    private static string? NullIfEmpty(string? value)
    {
        return ArtistNameHelper.NormalizeStringCore(value);
    }

    private static string? GetLocalizedBiography(Dictionary<string, JsonElement>? extensionData, string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode) || extensionData is null)
            return null;

        // Ensure we only use the 2-letter ISO 639-1 code (e.g. "de-DE" -> "DE")
        var isoCode = languageCode.Length > 2 ? languageCode[..2] : languageCode;

        // API checks: strBiographyDE, strBiographyFR, etc.
        var targetKey = $"strBiography{isoCode.ToUpperInvariant()}";

        // Use case-insensitive lookup
        var entry = extensionData.FirstOrDefault(x => x.Key.Equals(targetKey, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(entry.Key) && entry.Value.ValueKind == JsonValueKind.String)
        {
            var bio = entry.Value.GetString();
            if (!string.IsNullOrWhiteSpace(bio))
                return bio;
        }

        return null;
    }
}
