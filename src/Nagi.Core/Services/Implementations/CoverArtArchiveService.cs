using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Reads album covers from the Cover Art Archive, the image store that MusicBrainz releases link to.
/// </summary>
public class CoverArtArchiveService : ICoverArtArchiveService, IDisposable
{
    private const string BaseUrl = "https://coverartarchive.org";

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<CoverArtArchiveService> _logger;

    public CoverArtArchiveService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        ILogger<CoverArtArchiveService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _logger = logger;
    }

    public void Dispose() => _httpClient.Dispose();

    public Task<ServiceResult<string>> GetReleaseCoverUrlAsync(string releaseId, CancellationToken cancellationToken = default) =>
        GetFrontCoverUrlAsync("release", releaseId, cancellationToken);

    public Task<ServiceResult<string>> GetReleaseGroupCoverUrlAsync(string releaseGroupId, CancellationToken cancellationToken = default) =>
        GetFrontCoverUrlAsync("release-group", releaseGroupId, cancellationToken);

    private Task<ServiceResult<string>> GetFrontCoverUrlAsync(string entity, string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult(ServiceResult<string>.FromSuccessNotFound());

        return _pipelines.GetJsonAsync<CoverArtListing, string>(_httpClient, ServiceProviderIds.CoverArtArchive,
            $"{BaseUrl}/{entity}/{Uri.EscapeDataString(id)}",
            listing =>
            {
                var front = listing.Images?.FirstOrDefault(image => image.Front);
                // The 1200px thumbnail keeps downloads small; the original can be a huge scan.
                var imageUrl = front?.Thumbnails?.GetValueOrDefault("1200")
                               ?? front?.Thumbnails?.GetValueOrDefault("large")
                               ?? front?.Image;
                if (string.IsNullOrEmpty(imageUrl)) return ServiceResult<string>.FromSuccessNotFound();

                // Older listings link over plain HTTP; the archive serves the same files over HTTPS.
                return ServiceResult<string>.FromSuccess(
                    new UriBuilder(imageUrl) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri);
            },
            _logger, $"Cover Art Archive cover for {entity} {id}", cancellationToken);
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
