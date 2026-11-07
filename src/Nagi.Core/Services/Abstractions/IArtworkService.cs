using Nagi.Core.Library;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Artist images and album covers, from music folders and online sources, and the artist biographies looked up
///     with them.
/// </summary>
public interface IArtworkService
{
    /// <summary>
    ///     Gets the artist, first looking up a missing image or biography online if the artist was never looked up.
    /// </summary>
    Task<Artist?> GetArtistDetailsAsync(Guid artistId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gives the artist the image the user picked, which online lookups and folder images never replace.
    /// </summary>
    Task<bool> UpdateArtistImageAsync(Guid artistId, string localFilePath);

    /// <summary>
    ///     Removes the image the user picked, going back to a folder or online image when there is one.
    /// </summary>
    Task<bool> RemoveArtistImageAsync(Guid artistId);

    /// <summary>
    ///     Fills in missing artist images, biographies and album covers from the enabled online sources.
    ///     Returns at once if online metadata is turned off or a run is already in progress.
    /// </summary>
    Task StartMetadataBackgroundFetchAsync();
}
