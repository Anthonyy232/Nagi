using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Provides a public artwork URL for an album. Discord's image proxy cannot read Nagi's
///     locally cached cover art, so rich presence needs an https URL it can fetch itself.
/// </summary>
public interface IAlbumArtUrlResolver
{
    /// <returns>The artwork URL, or <c>null</c> when no artwork is available.</returns>
    Task<string?> GetArtworkUrlAsync(Song song, CancellationToken cancellationToken = default);
}
