using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Service for resolving artist identities via the MusicBrainz database.
///     Rate-limited to 1 request per second as per MusicBrainz API requirements.
/// </summary>
public class MusicBrainzService : IMusicBrainzService
{
    private const string BaseUrl = "https://musicbrainz.org/ws/2";
    private const string UserAgent = "Nagi/1.0 (+https://github.com/Anthonyy232/Nagi)";
    
    private static readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);
    private static DateTime _lastRequestTime = DateTime.MinValue;
    
    private readonly HttpClient _httpClient;
    private readonly ILogger<MusicBrainzService> _logger;

    public MusicBrainzService(IHttpClientFactory httpClientFactory, ILogger<MusicBrainzService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> SearchArtistAsync(string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return null;

        // Acquire semaphore and hold it through the entire request to prevent bursts
        await _rateLimitSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Enforce minimum 1 second between requests
            var elapsed = DateTime.UtcNow - _lastRequestTime;
            if (elapsed < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(TimeSpan.FromSeconds(1) - elapsed, cancellationToken).ConfigureAwait(false);
            }

            // Quote the artist name for multi-word names (Lucene syntax)
            var quotedName = $"\"{artistName}\"";
            var encodedName = Uri.EscapeDataString(quotedName);
            var url = $"{BaseUrl}/artist?query=artist:{encodedName}&limit=1&fmt=json";

            _logger.LogDebug("Searching MusicBrainz for artist: {ArtistName}", artistName);

            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // Update timestamp AFTER request completes
            _lastRequestTime = DateTime.UtcNow;

            // Handle rate limiting (503 per MusicBrainz docs)
            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                _logger.LogWarning("MusicBrainz rate limit hit (503). Request rejected for: {ArtistName}", artistName);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MusicBrainz search failed with status {StatusCode} for artist: {ArtistName}",
                    response.StatusCode, artistName);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<MusicBrainzSearchResult>(
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var artist = result?.Artists?.FirstOrDefault();
            if (artist is null)
            {
                _logger.LogDebug("No MusicBrainz match found for artist: {ArtistName}", artistName);
                return null;
            }

            // Verify the match is reasonably close (score > 80)
            if (artist.Score < 80)
            {
                _logger.LogDebug("MusicBrainz match score too low ({Score}) for artist: {ArtistName}",
                    artist.Score, artistName);
                return null;
            }

            _logger.LogInformation("Found MusicBrainz ID {MBID} for artist: {ArtistName}",
                artist.Id, artistName);
            
            return artist.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error searching MusicBrainz for artist: {ArtistName}", artistName);
            return null;
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    // DTOs for JSON deserialization
    private sealed class MusicBrainzSearchResult
    {
        [JsonPropertyName("artists")]
        public List<MusicBrainzArtist>? Artists { get; set; }
    }

    private sealed class MusicBrainzArtist
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("score")]
        public int Score { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
