using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;
using MaterialColorUtilities.Utils;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nagi.Core.Services.Implementations;

/// <summary>
/// Processes images using ImageSharp for resizing and color extraction.
/// </summary>
public class ImageSharpProcessor : IImageProcessor {
    private const int CoverArtResizeDimension = 112;
    private readonly string _albumArtStoragePath;
    private readonly IFileSystemService _fileSystem;

    // A per-instance, async-compatible lock to prevent race conditions when writing the same file.
    // Making this an instance field (not static) is crucial for test isolation.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileWriteSemaphores = new();

    public ImageSharpProcessor(IPathConfiguration pathConfig, IFileSystemService fileSystem) {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _albumArtStoragePath = pathConfig.AlbumArtCachePath;

        // Ensure the cache directory exists on startup.
        try {
            _fileSystem.CreateDirectory(_albumArtStoragePath);
        }
        catch (Exception ex) {
            Debug.WriteLine(
                $"[ImageProcessor] FATAL: Failed to create Album Art directory '{_albumArtStoragePath}'. Reason: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<(string? uri, string? lightSwatchId, string? darkSwatchId)> SaveCoverArtAndExtractColorsAsync(
        byte[] pictureData, string albumTitle, string artistName) {
        var stableId = GenerateStableId(artistName, albumTitle);
        var filename = $"{stableId}.jpg";
        var fullPath = _fileSystem.Combine(_albumArtStoragePath, filename);

        await WriteImageToFileAsync(pictureData, fullPath);

        var (lightHex, darkHex) = ExtractColorSwatches(pictureData);

        return (fullPath, lightHex, darkHex);
    }

    /// <summary>
    /// Writes image data to a file if it doesn't already exist, using a semaphore for thread safety.
    /// </summary>
    private async Task WriteImageToFileAsync(byte[] pictureData, string fullPath) {
        if (_fileSystem.FileExists(fullPath)) return;

        var semaphore = _fileWriteSemaphores.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync();
        try {
            // Double-check after acquiring the lock in case another thread wrote the file while we waited.
            if (!_fileSystem.FileExists(fullPath)) {
                await _fileSystem.WriteAllBytesAsync(fullPath, pictureData);
            }
        }
        finally {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Extracts primary light and dark theme colors from image data.
    /// </summary>
    /// <returns>A tuple containing the light and dark hex color codes.</returns>
    private (string? lightHex, string? darkHex) ExtractColorSwatches(byte[] pictureData) {
        try {
            using var image = Image.Load<Rgba32>(pictureData);
            image.Mutate(x => x.Resize(new ResizeOptions {
                Size = new Size(CoverArtResizeDimension, CoverArtResizeDimension),
                Mode = ResizeMode.Max
            }));

            var pixels = new uint[image.Width * image.Height];
            image.CopyPixelDataTo(MemoryMarshal.AsBytes(pixels.AsSpan()));

            // Convert from ImageSharp's RGBA format to the ARGB integer format expected by the Material library.
            for (var i = 0; i < pixels.Length; i++) {
                pixels[i] = pixels[i] & 0xFF00FF00 | (pixels[i] & 0x00FF0000) >> 16 | (pixels[i] & 0x000000FF) << 16;
            }

            var seedColor = ImageUtils.ColorsFromImage(pixels).FirstOrDefault();
            if (seedColor == default) return (null, null);

            var corePalette = CorePalette.Of(seedColor);
            var lightScheme = new LightSchemeMapper().Map(corePalette);
            var darkScheme = new DarkSchemeMapper().Map(corePalette);

            var lightHex = (lightScheme.Primary & 0x00FFFFFF).ToString("x6");
            var darkHex = (darkScheme.Primary & 0x00FFFFFF).ToString("x6");

            return (lightHex, darkHex);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ImageProcessor] Warning: Failed to extract colors. Reason: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Generates a stable SHA256 hash from artist and album names to create a consistent filename.
    /// </summary>
    private static string GenerateStableId(string artistName, string albumTitle) {
        using var sha = SHA256.Create();
        var textToHash = $"{artistName}_{albumTitle}";
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(textToHash));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}