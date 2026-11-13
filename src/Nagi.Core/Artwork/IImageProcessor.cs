namespace Nagi.Core.Artwork;

public interface IImageProcessor
{
    /// <summary>
    ///     Saves cover art to the cache and extracts the theme colors of its primary color. Identical images share one
    ///     file, and images larger than 600x600 are scaled down.
    /// </summary>
    /// <returns>The saved file and the light and dark theme colors as hex, all null if processing fails.</returns>
    Task<(string? uri, string? lightSwatchId, string? darkSwatchId)> SaveCoverArtAndExtractColorsAsync(
        byte[] pictureData);

    /// <summary>Scales an image larger than 600x600 down to fit, keeping its aspect ratio, and encodes it as JPEG.</summary>
    /// <returns>The JPEG, or the original bytes when they need no scaling or can't be read.</returns>
    Task<byte[]> ProcessImageBytesAsync(byte[] imageData);
}
