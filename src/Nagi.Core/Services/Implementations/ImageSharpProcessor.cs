using System.Collections.Concurrent;
using System.Security.Cryptography;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;
using MaterialColorUtilities.Utils;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     An image processor implementation using ImageSharp for resizing and color extraction.
///     Uses content-based hashing to deduplicate identical images across multiple songs,
///     and resizes large images to reduce disk usage.
/// </summary>
public class ImageSharpProcessor : IImageProcessor
{
    /// <summary>
    ///     Maximum dimension (width or height) for cached images. Larger images are resized
    ///     while preserving aspect ratio. 800px provides good quality for high-DPI displays
    ///     while significantly reducing storage for large album art (4000x4000+).
    /// </summary>
    private const int CachedImageMaxDimension = 800;

    /// <summary>
    ///     Dimension used for color extraction. A small size is sufficient for palette analysis
    ///     and makes the extraction much faster.
    /// </summary>
    private const int ColorExtractionDimension = 112;

    private readonly string _albumArtStoragePath;
    private readonly IFileSystemService _fileSystem;
    private readonly ILogger<ImageSharpProcessor> _logger;

    /// <summary>
    ///     Tracks in-flight save operations to prevent race conditions when multiple threads
    ///     try to save the same image content simultaneously. Each key is a content hash,
    ///     and the value is a lazy task that ensures only one save operation runs per hash.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlightSaves = new();

    /// <summary>
    ///     Static constructor to configure ImageSharp memory settings globally.
    ///     This helps reduce Large Object Heap fragmentation during high-volume image processing.
    /// </summary>
    static ImageSharpProcessor()
    {
        // Configure ImageSharp to use a more memory-efficient allocator
        // This helps reduce LOH fragmentation during batch processing
        Configuration.Default.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            // Limit maximum pool size to prevent excessive memory retention
            MaximumPoolSizeMegabytes = 32
        });
    }

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
        byte[] pictureData)
    {
        if (pictureData.Length == 0)
            return (null, null, null);

        try
        {
            // Generate a content-based hash for deduplication
            var contentHash = GenerateContentHash(pictureData);
            // Use standardized naming convention for fetched/processed images
            var filename = $"{contentHash}.fetched.jpg";
            var fullPath = _fileSystem.Combine(_albumArtStoragePath, filename);

            // Save with deduplication - handles both file existence check and concurrent access
            await SaveImageWithDeduplicationAsync(contentHash, fullPath, pictureData);

            // Extract colors (always from original data for best accuracy)
            var (lightHex, darkHex) = ExtractColorSwatches(pictureData);
            return (fullPath, lightHex, darkHex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save cover art and extract colors.");
            return (null, null, null);
        }
    }

    /// <summary>
    ///     Saves the image to disk, handling concurrent attempts to save the same content.
    ///     Uses a ConcurrentDictionary with Lazy to ensure only one thread actually 
    ///     performs the save for a given content hash.
    /// </summary>
    private async Task SaveImageWithDeduplicationAsync(string contentHash, string fullPath, byte[] pictureData)
    {
        // Fast path: if file already exists, skip the save operation entirely
        if (_fileSystem.FileExists(fullPath))
            return;

        // Use Lazy<Task> to ensure the save task is only started once per content hash,
        // even if multiple threads call GetOrAdd simultaneously.
        var lazyTask = _inFlightSaves.GetOrAdd(contentHash,
            _ => new Lazy<Task<string>>(() => SaveImageToDiskAsync(fullPath, pictureData)));

        try
        {
            await lazyTask.Value;
        }
        finally
        {
            // Remove from in-flight tracking once complete (whether success or failure)
            _inFlightSaves.TryRemove(contentHash, out _);
        }
    }

    /// <summary>
    ///     Actually performs the image loading, resizing, and saving to disk.
    ///     Uses atomic file write via temp file to prevent partial writes.
    /// </summary>
    private async Task<string> SaveImageToDiskAsync(string fullPath, byte[] pictureData)
    {
        // Double-check file doesn't exist (another thread may have created it while we were waiting)
        if (_fileSystem.FileExists(fullPath))
            return fullPath;

        using var image = Image.Load<Rgba32>(pictureData);

        // Only resize if larger than max dimension
        if (image.Width > CachedImageMaxDimension || image.Height > CachedImageMaxDimension)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(CachedImageMaxDimension, CachedImageMaxDimension),
                Mode = ResizeMode.Max // Preserves aspect ratio, fits within bounds
            }));

            _logger.LogDebug("Resized album art from original to {Width}x{Height} for caching.",
                image.Width, image.Height);
        }

        // Save to memory stream first, then write bytes to disk atomically via temp file
        using var memoryStream = new MemoryStream();
        await image.SaveAsJpegAsync(memoryStream);
        var imageBytes = memoryStream.ToArray();

        // Write to a temp file first, then move atomically to avoid partial writes
        var tempPath = fullPath + ".tmp";
        await _fileSystem.WriteAllBytesAsync(tempPath, imageBytes);
        
        // Atomic move - if target exists now (race), just delete our temp file
        try
        {
            _fileSystem.MoveFile(tempPath, fullPath, overwrite: false);
        }
        catch (IOException)
        {
            // File already exists (another thread won the race), delete our temp file
            try { _fileSystem.DeleteFile(tempPath); } catch { /* ignore */ }
        }
        
        return fullPath;
    }

    /// <summary>
    ///     Extracts primary color swatches for light and dark themes from image data.
    ///     Uses a small resized version for faster processing.
    /// </summary>
    private (string? lightHex, string? darkHex) ExtractColorSwatches(byte[] pictureData)
    {
        try
        {
            using var image = Image.Load<Rgba32>(pictureData);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(ColorExtractionDimension, ColorExtractionDimension),
                Mode = ResizeMode.Max
            }));

            var pixels = new uint[image.Width * image.Height];
            image.CopyPixelDataTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));

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
    ///     Generates a content-based hash from the image data. This ensures that identical
    ///     images (e.g., same embedded art across all songs in an album) share a single
    ///     cached file, dramatically reducing disk usage.
    /// </summary>
    private static string GenerateContentHash(byte[] pictureData)
    {
        var hashBytes = SHA256.HashData(pictureData);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}