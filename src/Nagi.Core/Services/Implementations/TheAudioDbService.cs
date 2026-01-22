using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Helpers;

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
///     Service for fetching artist metadata from TheAudioDB.
///     Rate-limited to 30 requests per minute (free tier).
/// </summary>
public class TheAudioDbService : ITheAudioDbService, IDisposable
{
    private const string BaseUrl = "https://www.theaudiodb.com/api/v1/json";
    private const string ApiKeyName = ServiceProviderIds.TheAudioDb;
    private const int MaxRetries = 3;
    private const int RateLimitDelayMultiplier = 5;

    // Rate limiting: 30 req/min = 1 request per 2 seconds
    private static readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);
    private static DateTime _lastRequestTime = DateTime.MinValue;
    private static readonly TimeSpan _minRequestInterval = TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<TheAudioDbService> _logger;

    private volatile bool _isApiDisabled;
    private bool _isDisposed;

    public TheAudioDbService(
        IHttpClientFactory httpClientFactory,
        IApiKeyService apiKeyService,
        ILogger<TheAudioDbService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    /// <summary>
    ///     Releases the resources used by the <see cref="TheAudioDbService" />.
    /// </summary>
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(musicBrainzId))
            return ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound();

        if (_isApiDisabled)
            return ServiceResult<TheAudioDbArtistInfo>.FromPermanentError("TheAudioDB API is disabled for this session.");

        var operationName = $"TheAudioDB metadata for MBID {musicBrainzId}";

        try
        {
            var result = await HttpRetryHelper.ExecuteWithRetryAsync<ServiceResult<TheAudioDbArtistInfo>>(
                async attempt =>
                {
                    // Acquire semaphore for rate limiting - only held during HTTP request
                    await _rateLimitSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        // Enforce minimum delay between requests
                        var elapsed = DateTime.UtcNow - _lastRequestTime;
                        if (elapsed < _minRequestInterval)
                        {
                            await Task.Delay(_minRequestInterval - elapsed, cancellationToken).ConfigureAwait(false);
                        }

                        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(apiKey))
                        {
                            _logger.LogWarning("TheAudioDB API key not available.");
                            return RetryResult<ServiceResult<TheAudioDbArtistInfo>>.Success(
                                ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError("API key not available."));
                        }

                        var url = $"{BaseUrl}/{apiKey}/artist-mb.php?i={musicBrainzId}";
                        _logger.LogDebug("Fetching TheAudioDB metadata for MBID: {MBID} (Attempt {Attempt}/{MaxRetries})", 
                            musicBrainzId, attempt, MaxRetries);

                        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

                        // Update timestamp AFTER request completes
                        _lastRequestTime = DateTime.UtcNow;

                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            _logger.LogDebug("No TheAudioDB data found for MBID: {MBID}", musicBrainzId);
                            return RetryResult<ServiceResult<TheAudioDbArtistInfo>>.Success(
                                ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound());
                        }

                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            _logger.LogWarning("TheAudioDB rate limit reached for MBID: {MBID}. Attempt {Attempt}/{MaxRetries}", 
                                musicBrainzId, attempt, MaxRetries);
                            
                            if (attempt >= MaxRetries)
                            {
                                _logger.LogError("TheAudioDB rate limit reached repeatedly. Disabling for this session.");
                                _isApiDisabled = true;
                                return RetryResult<ServiceResult<TheAudioDbArtistInfo>>.Success(
                                    ServiceResult<TheAudioDbArtistInfo>.FromPermanentError("Rate limited."));
                            }

                            return RetryResult<ServiceResult<TheAudioDbArtistInfo>>.RateLimitFailure(RateLimitDelayMultiplier);
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("TheAudioDB request failed with status {StatusCode} for MBID: {MBID}. Attempt {Attempt}/{MaxRetries}", 
                                response.StatusCode, musicBrainzId, attempt, MaxRetries);
                            
                            if (HttpRetryHelper.IsRetryableStatusCode(response.StatusCode))
                                return RetryResult<ServiceResult<TheAudioDbArtistInfo>>.TransientFailure();

                            return RetryResult<ServiceResult<TheAudioDbArtistInfo>>.Success(
                                ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError($"HTTP {response.StatusCode}"));
                        }

                        var apiResult = await response.Content.ReadFromJsonAsync<TheAudioDbResponse>(
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        var artist = apiResult?.Artists?.FirstOrDefault();
                        if (artist is null)
                        {
                            _logger.LogDebug("TheAudioDB returned null/empty artist data for MBID: {MBID}", musicBrainzId);
                            return RetryResult<ServiceResult<TheAudioDbArtistInfo>>.Success(
                                ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound());
                        }

                        // Select the best available fanart image
                        var fanartUrl = GetFirstNonEmpty(artist.ArtistFanart, artist.ArtistFanart2, artist.ArtistFanart3);

                        var info = new TheAudioDbArtistInfo(
                            Biography: SanitizeBiography(artist.BiographyEN),
                            ThumbUrl: NullIfEmpty(artist.ArtistThumb),
                            FanartUrl: fanartUrl,
                            WideThumbUrl: NullIfEmpty(artist.ArtistWideThumb),
                            LogoUrl: NullIfEmpty(artist.ArtistLogo)
                        );

                        // Check if we actually got any usable data
                        if (info.Biography is null && info.ThumbUrl is null && info.FanartUrl is null &&
                            info.WideThumbUrl is null && info.LogoUrl is null)
                        {
                            _logger.LogDebug("TheAudioDB returned empty metadata for MBID: {MBID}", musicBrainzId);
                            return RetryResult<ServiceResult<TheAudioDbArtistInfo>>.Success(
                                ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound());
                        }

                        _logger.LogInformation("Found TheAudioDB metadata for MBID: {MBID}", musicBrainzId);
                        return RetryResult<ServiceResult<TheAudioDbArtistInfo>>.Success(
                            ServiceResult<TheAudioDbArtistInfo>.FromSuccess(info));
                    }
                    finally
                    {
                        _rateLimitSemaphore.Release();
                    }
                },
                _logger,
                operationName,
                cancellationToken,
                MaxRetries
            ).ConfigureAwait(false);

            return result ?? ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError("Failed after exhausting retries.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error fetching TheAudioDB metadata for MBID: {MBID} after exhausting retries.", musicBrainzId);
            return ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError(ex.Message);
        }
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
}
