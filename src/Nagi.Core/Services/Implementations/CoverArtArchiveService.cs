using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Reads album covers from the Cover Art Archive, the image store that MusicBrainz releases link to.
/// </summary>
public class CoverArtArchiveService : ICoverArtArchiveService, IDisposable
{
    private const string BaseUrl = "https://coverartarchive.org";
    private const string UserAgent = "Nagi/1.0 (+https://github.com/Anthonyy232/Nagi)";

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<CoverArtArchiveService> _logger;

    public CoverArtArchiveService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        ILogger<CoverArtArchiveService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _pipelines = pipelines;
        _logger = logger;
    }

    public void Dispose() => _httpClient.Dispose();

    public Task<ServiceResult<string>> GetReleaseCoverUrlAsync(string releaseId, CancellationToken cancellationToken = default) =>
        GetFrontCoverUrlAsync("release", releaseId, cancellationToken);

    public Task<ServiceResult<string>> GetReleaseGroupCoverUrlAsync(string releaseGroupId, CancellationToken cancellationToken = default) =>
        GetFrontCoverUrlAsync("release-group", releaseGroupId, cancellationToken);

    private async Task<ServiceResult<string>> GetFrontCoverUrlAsync(string entity, string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ServiceResult<string>.FromSuccessNotFound();

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.CoverArtArchive))
            return ServiceResult<string>.FromTemporaryError();

        var url = $"{BaseUrl}/{entity}/{Uri.EscapeDataString(id)}";
        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.CoverArtArchive,
            async ct =>
            {
                _logger.LogDebug("Fetching Cover Art Archive images for {Entity} {Id}", entity, id);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return ServiceResult<string>.FromSuccessNotFound();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Cover Art Archive request failed with status {StatusCode} for {Entity} {Id}",
                        response.StatusCode, entity, id);
                    return ServiceResult<string>.FromTemporaryError();
                }

                var listing = await response.Content.ReadFromJsonAsync<CoverArtListing>(ct).ConfigureAwait(false);
                var front = listing?.Images?.FirstOrDefault(image => image.Front);
                // The 1200px thumbnail keeps downloads small; the original can be a huge scan.
                var imageUrl = front?.Thumbnails?.GetValueOrDefault("1200")
                               ?? front?.Thumbnails?.GetValueOrDefault("large")
                               ?? front?.Image;
                if (string.IsNullOrEmpty(imageUrl)) return ServiceResult<string>.FromSuccessNotFound();

                // Older listings link over plain HTTP; the archive serves the same files over HTTPS.
                return ServiceResult<string>.FromSuccess(new UriBuilder(imageUrl) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri);
            },
            fallback: ServiceResult<string>.FromTemporaryError(),
            _logger,
            $"cover lookup for {entity} {id}",
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class CoverArtListing
    {
        [JsonPropertyName("images")]
        public List<CoverArtImage>? Images { get; set; }
    }

    private sealed class CoverArtImage
    {
        [JsonPropertyName("front")]
        public bool Front { get; set; }

        [JsonPropertyName("image")]
        public string? Image { get; set; }

        [JsonPropertyName("thumbnails")]
        public Dictionary<string, string>? Thumbnails { get; set; }
    }
}
