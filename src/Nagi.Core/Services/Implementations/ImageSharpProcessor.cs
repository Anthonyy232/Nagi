using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;
using MaterialColorUtilities.Utils;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     An image processor implementation using ImageSharp for resizing and color extraction.
///     This service is designed to be simple and fast, relying on the calling service
///     to handle concurrency control (i.e., not calling methods for the same image
///     simultaneously from multiple threads).
/// </summary>
public class ImageSharpProcessor : IImageProcessor
{
    private const int CoverArtResizeDimension = 112;
    private readonly string _albumArtStoragePath;
    private readonly IFileSystemService _fileSystem;
    private readonly ILogger<ImageSharpProcessor> _logger;

    public ImageSharpProcessor(IPathConfiguration pathConfig, IFileSystemService fileSystem,
        ILogger<ImageSharpProcessor> logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger;
        _albumArtStoragePath = pathConfig.AlbumArtCachePath;

        try
        {
            _fileSystem.CreateDirectory(_albumArtStoragePath);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to create Album Art directory at '{AlbumArtPath}'.", _albumArtStoragePath);
        }
    }

    /// <inheritdoc />
    public async Task<(string? uri, string? lightSwatchId, string? darkSwatchId)> SaveCoverArtAndExtractColorsAsync(
        byte[] pictureData, string songFilePath)
    {
        var stableId = GenerateStableId(songFilePath);
        var filename = $"{stableId}.jpg";
        var fullPath = _fileSystem.Combine(_albumArtStoragePath, filename);

        if (_fileSystem.FileExists(fullPath))
        {
            var (lightHex, darkHex) = ExtractColorSwatches(pictureData);
            return (fullPath, lightHex, darkHex);
        }

        await _fileSystem.WriteAllBytesAsync(fullPath, pictureData);
        var (lightSwatchHex, darkSwatchHex) = ExtractColorSwatches(pictureData);

        return (fullPath, lightSwatchHex, darkSwatchHex);
    }

    /// <summary>
    ///     Extracts primary color swatches for light and dark themes from image data.
    /// </summary>
    /// <param name="pictureData">The raw byte data of the image.</param>
    /// <returns>A tuple containing the hex color codes for the light and dark swatches.</returns>
    private (string? lightHex, string? darkHex) ExtractColorSwatches(byte[] pictureData)
    {
        try
        {
            using var image = Image.Load<Rgba32>(pictureData);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(CoverArtResizeDimension, CoverArtResizeDimension),
                Mode = ResizeMode.Max
            }));

            var pixels = new uint[image.Width * image.Height];
            image.CopyPixelDataTo(MemoryMarshal.AsBytes(pixels.AsSpan()));

            // Convert ImageSharp's RGBA format to MaterialColorUtilities' ARGB format
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (pixels[i] & 0xFF00FF00) | ((pixels[i] & 0x00FF0000) >> 16) |
                            ((pixels[i] & 0x000000FF) << 16);

            var seedColor = ImageUtils.ColorsFromImage(pixels).FirstOrDefault();
            if (seedColor == default)
            {
                _logger.LogWarning("Could not determine a seed color from the image.");
                return (null, null);
            }

            var corePalette = CorePalette.Of(seedColor);
            var lightScheme = new LightSchemeMapper().Map(corePalette);
            var darkScheme = new DarkSchemeMapper().Map(corePalette);

            var lightHex = (lightScheme.Primary & 0x00FFFFFF).ToString("x6");
            var darkHex = (darkScheme.Primary & 0x00FFFFFF).ToString("x6");

            return (lightHex, darkHex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract colors from album art.");
            return (null, null);
        }
    }

    /// <summary>
    ///     Generates a content-based identifier from the song's file path to ensure each song
    ///     has its own unique cached cover art file.
    /// </summary>
    private static string GenerateStableId(string songFilePath)
    {
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(songFilePath));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}