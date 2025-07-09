using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;
using MaterialColorUtilities.Utils;
using Nagi.Helpers;
using Nagi.Services.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nagi.Services.Implementations;

/// <summary>
///     Processes images using ImageSharp for resizing and SixLabors.ImageSharp for color extraction.
/// </summary>
public class ImageSharpProcessor : IImageProcessor {
    private const int CoverArtResizeDimension = 112;
    private readonly string _albumArtStoragePath;
    private readonly IFileSystemService _fileSystem;
    public ImageSharpProcessor(PathConfiguration pathConfig, IFileSystemService fileSystem) {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

        // Get the correct path from the central configuration.
        _albumArtStoragePath = pathConfig.AlbumArtCachePath;

        // The directory is already created by PathConfiguration, but a safety check doesn't hurt.
        try {
            _fileSystem.CreateDirectory(_albumArtStoragePath);
        }
        catch (Exception ex) {
            Debug.WriteLine(
                $"[ImageProcessor] FATAL: Failed to ensure Album Art Storage Directory exists at '{_albumArtStoragePath}'. Reason: {ex.Message}");
        }
    }

    public async Task<(string? uri, string? lightSwatchId, string? darkSwatchId)> SaveCoverArtAndExtractColorsAsync(
        byte[] pictureData, string albumTitle, string artistName) {
        string? savedUri = null;
        string? lightHex = null;
        string? darkHex = null;

        try {
            // Generate a stable, deterministic filename using a hash.
            var stableId = GenerateStableHash($"{artistName}_{albumTitle}");
            var filename = $"{stableId}.jpg";
            var fullPath = _fileSystem.Combine(_albumArtStoragePath, filename);

            await _fileSystem.WriteAllBytesAsync(fullPath, pictureData);
            savedUri = fullPath;

            // Resize the image in memory for faster color analysis.
            using var image = Image.Load<Rgba32>(pictureData);
            image.Mutate(x => x.Resize(new ResizeOptions {
                Size = new Size(CoverArtResizeDimension, CoverArtResizeDimension),
                Mode = ResizeMode.Max
            }));

            var pixels = new uint[image.Width * image.Height];
            image.CopyPixelDataTo(MemoryMarshal.AsBytes(pixels.AsSpan()));

            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (pixels[i] & 0xFF00FF00) | ((pixels[i] & 0x00FF0000) >> 16) |
                            ((pixels[i] & 0x000000FF) << 16);

            var bestColors = ImageUtils.ColorsFromImage(pixels);
            if (bestColors.Count == 0) return (savedUri, null, null);

            var seedColor = bestColors.First();
            var corePalette = CorePalette.Of(seedColor);

            var lightScheme = new LightSchemeMapper().Map(corePalette);
            var darkScheme = new DarkSchemeMapper().Map(corePalette);

            lightHex = (lightScheme.Primary & 0x00FFFFFF).ToString("x6");
            darkHex = (darkScheme.Primary & 0x00FFFFFF).ToString("x6");
        }
        catch (Exception ex) {
            Debug.WriteLine(
                $"[ImageProcessor] Warning: Failed to save/process cover art for '{albumTitle}'. Reason: {ex.Message}");
            return (savedUri, null, null);
        }

        return (savedUri, lightHex, darkHex);
    }

    private static string GenerateStableHash(string text) {
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(hashBytes.Length * 2);
        foreach (var b in hashBytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}