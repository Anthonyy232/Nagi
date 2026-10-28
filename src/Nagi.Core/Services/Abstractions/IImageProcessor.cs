namespace Nagi.Core.Services.Abstractions;

public interface IImageProcessor
{
    /// <summary>
    ///     Saves cover art from a byte array to local storage and extracts primary color swatches.
    ///     Uses content-based hashing to deduplicate identical images across multiple songs.
    ///     Images larger than 600x600 are resized to reduce disk usage.
    /// </summary>
    /// <returns>
    ///     A tuple containing the local file URI of the saved image, and hex color codes for the
    ///     light and dark theme primary colors. All values can be null if processing fails.
    /// </returns>
    Task<(string? uri, string? lightSwatchId, string? darkSwatchId)> SaveCoverArtAndExtractColorsAsync(
        byte[] pictureData);

    /// <summary>
    ///     Processes image bytes: resizes if larger than max dimension, converts to JPEG.
    ///     Preserves aspect ratio using ResizeMode.Max (fits within bounds without distortion).
    /// </summary>
    /// <param name="maxDimension">Maximum width or height.</param>
    /// <returns>Processed image bytes as JPEG, or original bytes if no resizing needed.</returns>
    Task<byte[]> ProcessImageBytesAsync(byte[] imageData, int maxDimension = 600);
}
