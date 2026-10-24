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

    public Task<ServiceResult<string>> SearchArtistAsync(string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(ServiceResult<string>.FromSuccessNotFound());

        return SearchAsync($"artist?query=artist:{Phrase(artistName)}", result => result.Artists,
            minimumScore: 80, $"artist: {artistName}", cancellationToken);
    }

    // Escape Lucene's quoted-phrase syntax before URL encoding.
    private static string Phrase(string value) =>
        Uri.EscapeDataString($"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");

    private async Task<ServiceResult<string>> SearchAsync(string pathAndQuery,
        Func<MusicBrainzSearchResult, List<MusicBrainzMatch>?> selectMatches, int minimumScore, string subject,
        CancellationToken cancellationToken)
    {
        if (_pipelines.IsCircuitOpen(ServiceProviderIds.MusicBrainz))
        {
            _logger.LogDebug("MusicBrainz circuit is open; skipping lookup for {Subject}.", subject);
            return ServiceResult<string>.FromTemporaryError();
        }

        var url = $"{BaseUrl}/{pathAndQuery}&limit=1&fmt=json";

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.MusicBrainz,
            async ct =>
            {
                _logger.LogDebug("Searching MusicBrainz for {Subject}", subject);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("MusicBrainz search failed with status {StatusCode} for {Subject}",
                        response.StatusCode, subject);
                    return ServiceResult<string>.FromTemporaryError();
                }

                var result = await response.Content.ReadFromJsonAsync<MusicBrainzSearchResult>(cancellationToken: ct)
                    .ConfigureAwait(false);

                var match = result is null ? null : selectMatches(result)?.FirstOrDefault();
                if (match is null)
                {
                    _logger.LogDebug("No MusicBrainz match found for {Subject}", subject);
                    return ServiceResult<string>.FromSuccessNotFound();
                }

                if (match.Score < minimumScore)
                {
                    _logger.LogDebug("MusicBrainz match score too low ({Score}) for {Subject}", match.Score, subject);
                    return ServiceResult<string>.FromSuccessNotFound();
                }

                _logger.LogInformation("Found MusicBrainz ID {MBID} for {Subject}", match.Id, subject);
                return ServiceResult<string>.FromSuccess(match.Id);
            },
            fallback: ServiceResult<string>.FromTemporaryError(),
            _logger,
            $"search for {subject}",
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class MusicBrainzSearchResult
    {
        [JsonPropertyName("artists")]
        public List<MusicBrainzMatch>? Artists { get; set; }
    }

    private sealed class MusicBrainzMatch
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("score")]
        public int Score { get; set; }
    }
}
