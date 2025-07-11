using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
/// Processes images using ImageSharp for resizing and color extraction.
/// </summary>
public class ImageSharpProcessor : IImageProcessor {
    private const int CoverArtResizeDimension = 112;
    private readonly string _albumArtStoragePath;
    private readonly IFileSystemService _fileSystem;

    //
    // Provides async-compatible, per-file locking to prevent race conditions when multiple
    // threads attempt to write the same album art file simultaneously. Using a dictionary
    // of semaphores is more granular and performant than a single global lock.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileWriteSemaphores = new();

    public ImageSharpProcessor(PathConfiguration pathConfig, IFileSystemService fileSystem) {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _albumArtStoragePath = pathConfig.AlbumArtCachePath;
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
        string? lightHex = null;
        string? darkHex = null;

        var stableId = GenerateStableHash($"{artistName}_{albumTitle}");
        var filename = $"{stableId}.jpg";
        var fullPath = _fileSystem.Combine(_albumArtStoragePath, filename);

        //
        // Acquire a semaphore specific to this file path for an async-compatible lock.
        var semaphore = _fileWriteSemaphores.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync();
        try {
            if (!_fileSystem.FileExists(fullPath)) {
                await _fileSystem.WriteAllBytesAsync(fullPath, pictureData);
            }
        }
        finally {
            semaphore.Release();
        }

        try {
            //
            // Color extraction can happen outside the lock, as it only reads data.
            using var image = Image.Load<Rgba32>(pictureData);
            image.Mutate(x => x.Resize(new ResizeOptions {
                Size = new Size(CoverArtResizeDimension, CoverArtResizeDimension),
                Mode = ResizeMode.Max
            }));

            var pixels = new uint[image.Width * image.Height];
            image.CopyPixelDataTo(MemoryMarshal.AsBytes(pixels.AsSpan()));

            //
            // Convert from RGBA to ARGB format for the color utility library.
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (pixels[i] & 0xFF00FF00) | ((pixels[i] & 0x00FF0000) >> 16) |
                            ((pixels[i] & 0x000000FF) << 16);

            var bestColors = ImageUtils.ColorsFromImage(pixels);
            if (bestColors.Count > 0) {
                var seedColor = bestColors.First();
                var corePalette = CorePalette.Of(seedColor);
                var lightScheme = new LightSchemeMapper().Map(corePalette);
                var darkScheme = new DarkSchemeMapper().Map(corePalette);
                lightHex = (lightScheme.Primary & 0x00FFFFFF).ToString("x6");
                darkHex = (darkScheme.Primary & 0x00FFFFFF).ToString("x6");
            }
        }
        catch (Exception ex) {
            Debug.WriteLine(
                $"[ImageProcessor] Warning: Failed to process cover art for '{albumTitle}'. Reason: {ex.Message}");
            //
            // Return the file path even if color extraction fails, so the album art can still be displayed.
            return (fullPath, null, null);
        }

        return (fullPath, lightHex, darkHex);
    }

    private static string GenerateStableHash(string text) {
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(hashBytes.Length * 2);
        foreach (var b in hashBytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}