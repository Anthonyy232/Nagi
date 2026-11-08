using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Metadata;
using Nagi.Presentation.Common;

namespace Nagi.Presentation.Lists;

/// <summary>
///     Changing and removing an artist's custom image, shared by the artist grid and the artist page.
/// </summary>
internal static class ArtistImageActions
{
    /// <summary>Lets the user pick an image file and stores it as the artist's custom image.</summary>
    public static Task ChangeAsync(Guid artistId, IUIService uiService, IArtworkService artwork, ILogger logger,
        Func<bool>? isClosed = null) =>
        UserAction.RunAsync(async () =>
        {
            var localPath = await uiService.PickSingleFileAsync(FileExtensions.ImageFileExtensions);
            if (string.IsNullOrWhiteSpace(localPath)) return;
            logger.LogInformation("Changing the image of artist {ArtistId} to {Path}", artistId, localPath);
            await artwork.UpdateArtistImageAsync(artistId, localPath);
        }, logger, uiService, isClosed);

    public static Task RemoveAsync(Guid artistId, IUIService uiService, IArtworkService artwork, ILogger logger,
        Func<bool>? isClosed = null) =>
        UserAction.RunAsync(() => artwork.RemoveArtistImageAsync(artistId), logger, uiService, isClosed);
}
