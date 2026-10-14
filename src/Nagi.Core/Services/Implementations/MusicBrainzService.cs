using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Service for resolving artist identities via the MusicBrainz database.
///     Rate limiting (1 req/s as MusicBrainz requires), retry, and circuit-breaker policy
///     are handled by <see cref="IProviderPipelineProvider"/>.
/// </summary>
public class MusicBrainzService : IMusicBrainzService
{
    private const string BaseUrl = "https://musicbrainz.org/ws/2";
    private const string UserAgent = "Nagi/1.0 (+https://github.com/Anthonyy232/Nagi)";

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<MusicBrainzService> _logger;

    public MusicBrainzService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        ILogger<MusicBrainzService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _pipelines = pipelines;
        _logger = logger;
    }

    public async Task<ServiceResult<string>> SearchArtistAsync(string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return ServiceResult<string>.FromSuccessNotFound();

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.MusicBrainz))
        {
            _logger.LogDebug("MusicBrainz circuit is open; skipping lookup for {ArtistName}.", artistName);
            return ServiceResult<string>.FromTemporaryError();
        }

        // Escape Lucene's quoted-phrase syntax before URL encoding.
        var escapedName = artistName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var encodedName = Uri.EscapeDataString($"\"{escapedName}\"");
        var url = $"{BaseUrl}/artist?query=artist:{encodedName}&limit=1&fmt=json";

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.MusicBrainz,
            async ct =>
            {
                _logger.LogDebug("Searching MusicBrainz for artist: {ArtistName}", artistName);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("MusicBrainz search failed with status {StatusCode} for artist: {ArtistName}",
                        response.StatusCode, artistName);
                    return ServiceResult<string>.FromTemporaryError();
                }

                var result = await response.Content.ReadFromJsonAsync<MusicBrainzSearchResult>(cancellationToken: ct)
                    .ConfigureAwait(false);

                var artist = result?.Artists?.FirstOrDefault();
                if (artist is null)
                {
                    _logger.LogDebug("No MusicBrainz match found for artist: {ArtistName}", artistName);
                    return ServiceResult<string>.FromSuccessNotFound();
                }

                if (artist.Score < 80)
                {
                    _logger.LogDebug("MusicBrainz match score too low ({Score}) for artist: {ArtistName}",
                        artist.Score, artistName);
                    return ServiceResult<string>.FromSuccessNotFound();
                }

                _logger.LogInformation("Found MusicBrainz ID {MBID} for artist: {ArtistName}", artist.Id, artistName);
                return ServiceResult<string>.FromSuccess(artist.Id);
            },
            fallback: ServiceResult<string>.FromTemporaryError(),
            _logger,
            $"search for {artistName}",
            cancellationToken).ConfigureAwait(false);
    }

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
    }
}
