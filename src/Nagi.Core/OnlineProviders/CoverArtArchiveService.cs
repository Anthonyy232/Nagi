using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.OnlineProviders.Pipelines;

namespace Nagi.Core.OnlineProviders;

/// <summary>
///     Reads album covers from the Cover Art Archive, the image store that MusicBrainz releases link to.
/// </summary>
public class CoverArtArchiveService : ICoverArtArchiveService
{
    private const string BaseUrl = "https://coverartarchive.org";

    private readonly ProviderClient _client;

    public CoverArtArchiveService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        ILogger<CoverArtArchiveService> logger)
    {
        _client = new ProviderClient(ProviderCatalog.CoverArtArchive.Policy, httpClientFactory.CreateClient(), pipelines, logger);
    }

    public Task<ServiceResult<string>> GetReleaseCoverUrlAsync(string releaseId, CancellationToken cancellationToken = default) =>
        GetFrontCoverUrlAsync("release", releaseId, cancellationToken);

    public Task<ServiceResult<string>> GetReleaseGroupCoverUrlAsync(string releaseGroupId, CancellationToken cancellationToken = default) =>
        GetFrontCoverUrlAsync("release-group", releaseGroupId, cancellationToken);

    private Task<ServiceResult<string>> GetFrontCoverUrlAsync(string entity, string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult(ServiceResult<string>.FromSuccessNotFound());

        return _client.GetJsonAsync<CoverArtListing, string>($"{BaseUrl}/{entity}/{Uri.EscapeDataString(id)}",
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
            $"Cover Art Archive cover for {entity} {id}", cancellationToken);
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
