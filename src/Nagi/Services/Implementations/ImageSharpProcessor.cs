using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;
using MaterialColorUtilities.Utils;
using Nagi.Services.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nagi.Services.Implementations;

/// <summary>
///     Processes images using ImageSharp for resizing and SixLabors.ImageSharp for color extraction.
/// </summary>
public class ImageSharpProcessor : IImageProcessor
{
    private const int CoverArtResizeDimension = 112;
    private const int MaxFilenamePartLength = 50;
    private readonly string _albumArtStoragePath;
    private readonly IFileSystemService _fileSystem;

    public ImageSharpProcessor(IFileSystemService fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        var baseLocalAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _albumArtStoragePath = _fileSystem.Combine(baseLocalAppDataPath, "Nagi", "AlbumArt");

        try
        {
            if (!_fileSystem.DirectoryExists(_albumArtStoragePath)) _fileSystem.CreateDirectory(_albumArtStoragePath);
        }
        catch (Exception ex)
        {
            // A failure here is critical as the application cannot store essential image data.
            Debug.WriteLine(
                $"[ImageProcessor] FATAL: Failed to create Album Art Storage Directory '{_albumArtStoragePath}'. Reason: {ex.Message}");
        }
    }

    public async Task<(string? uri, string? lightSwatchId, string? darkSwatchId)> SaveCoverArtAndExtractColorsAsync(
        byte[] pictureData, string albumTitle, string artistName)
    {
        string? savedUri = null;
        string? lightHex = null;
        string? darkHex = null;

        try
        {
            // Generate a unique, filesystem-safe filename to avoid collisions and errors.
            var safeAlbum = SanitizeFilenamePart(albumTitle, "UnknownAlbum");
            var safeArtist = SanitizeFilenamePart(artistName, "UnknownArtist");
            var filename = $"{safeArtist}_{safeAlbum}_{Guid.NewGuid():N}.jpg";
            var fullPath = _fileSystem.Combine(_albumArtStoragePath, filename);

            await _fileSystem.WriteAllBytesAsync(fullPath, pictureData);
            savedUri = fullPath;

            // Resize the image in memory for faster color analysis.
            using var image = Image.Load<Rgba32>(pictureData);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(CoverArtResizeDimension, CoverArtResizeDimension),
                Mode = ResizeMode.Max
            }));

            var pixels = new uint[image.Width * image.Height];
            image.CopyPixelDataTo(MemoryMarshal.AsBytes(pixels.AsSpan()));

            // The color library expects ARGB (0xAARRGGBB). ImageSharp provides RGBA, which on a
            // little-endian system, loads into a uint as ABGR (0xAABBGGRR). This loop swaps R and B.
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (pixels[i] & 0xFF00FF00) | ((pixels[i] & 0x00FF0000) >> 16) |
                            ((pixels[i] & 0x000000FF) << 16);

            // Extract a list of the most suitable theme colors, ranked by a scoring algorithm.
            var bestColors = ImageUtils.ColorsFromImage(pixels);
            if (bestColors.Count == 0) return (savedUri, null, null);

            var seedColor = bestColors.First();
            var corePalette = CorePalette.Of(seedColor);

            var lightScheme = new LightSchemeMapper().Map(corePalette);
            var darkScheme = new DarkSchemeMapper().Map(corePalette);

            // Mask to remove the alpha channel before converting to a hex string (e.g., FFFFFF).
            lightHex = (lightScheme.Primary & 0x00FFFFFF).ToString("x6");
            darkHex = (darkScheme.Primary & 0x00FFFFFF).ToString("x6");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[ImageProcessor] Warning: Failed to save/process cover art for '{albumTitle}'. Reason: {ex.Message}");
            // Return the saved URI even if color extraction fails, so the app can still display the cover.
            return (savedUri, null, null);
        }

        return (savedUri, lightHex, darkHex);
    }

    /// <summary>
    ///     Sanitizes a string to be used as part of a filename.
    /// </summary>
    private static string SanitizeFilenamePart(string input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;

        var sanitized = string.Join("_", input.Split(Path.GetInvalidFileNameChars()));

        if (string.IsNullOrWhiteSpace(sanitized)) return fallback;

        return sanitized.Length > MaxFilenamePartLength ? sanitized.Substring(0, MaxFilenamePartLength) : sanitized;
    }
}