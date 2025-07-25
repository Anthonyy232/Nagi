using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Data;

/// <summary>
///     Provides data for the <see cref="ILibraryScanner.ArtistMetadataUpdated" /> event.
/// </summary>
public class ArtistMetadataUpdatedEventArgs : EventArgs
{
    public ArtistMetadataUpdatedEventArgs(Guid artistId, string? newLocalImageCachePath)
    {
        ArtistId = artistId;
        NewLocalImageCachePath = newLocalImageCachePath;
    }

    /// <summary>
    ///     The unique identifier of the artist that was updated.
    /// </summary>
    public Guid ArtistId { get; init; }

    /// <summary>
    ///     The new local file path for the artist's cached image.
    /// </summary>
    public string? NewLocalImageCachePath { get; init; }
}