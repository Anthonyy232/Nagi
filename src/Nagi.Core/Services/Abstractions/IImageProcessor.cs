﻿namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a contract for image processing tasks, such as saving cover art and extracting theme colors.
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    ///     Saves cover art from a byte array to local storage and extracts primary color swatches.
    /// </summary>
    /// <param name="pictureData">The raw byte data of the image file.</param>
    /// <param name="albumTitle">The album title, used for generating a unique filename.</param>
    /// <param name="artistName">The artist name, used for generating a unique filename.</param>
    /// <returns>
    ///     A tuple containing the local file URI of the saved image, and hex color codes for the
    ///     light and dark theme primary colors. All values can be null if processing fails.
    /// </returns>
    Task<(string? uri, string? lightSwatchId, string? darkSwatchId)> SaveCoverArtAndExtractColorsAsync(
        byte[] pictureData, string albumTitle, string artistName);
}