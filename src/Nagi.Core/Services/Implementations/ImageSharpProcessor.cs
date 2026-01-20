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
    ///     Standardized maximum dimension for all cached images.
    ///     600px provides crisp display at 3x DPI (160px * 3 = 480px with buffer)
    ///     while significantly reducing storage for large source images.
    /// </summary>
    private const int CachedImageMaxDimension = 600;

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
    /// <remarks>
    /// This method uses a filename-based color cache. The filename pattern is:
    /// <c>{hash}.{lightHex}.{darkHex}.fetched.jpg</c>
    /// This allows subsequent calls with the same image content to skip image decoding entirely
    /// by parsing the colors from the existing filename.
    /// </remarks>
    public async Task<(string? uri, string? lightSwatchId, string? darkSwatchId)> SaveCoverArtAndExtractColorsAsync(
        byte[] pictureData)
    {
        if (pictureData.Length == 0)
            return (null, null, null);

        try
        {
            // Generate a content-based hash for deduplication
            var contentHash = GenerateContentHash(pictureData);

            // Fast path: check if a file with this hash already exists (includes colors in filename)
            var existingFile = FindCachedFileByHash(contentHash);
            if (existingFile != null)
            {
                var (lightHex, darkHex) = ParseColorsFromFilename(existingFile);
                return (existingFile, lightHex, darkHex);
            }

            // Slow path: load image once, save and extract colors
            return await ProcessAndSaveNewImageAsync(contentHash, pictureData).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save cover art and extract colors.");
            return (null, null, null);
        }
    }

    /// <summary>
    ///     Finds an existing cached file by content hash prefix.
    ///     Returns the full path if found, null otherwise.
    /// </summary>
    private string? FindCachedFileByHash(string contentHash)
    {
        try
        {
            // Look for files matching the hash prefix pattern
            var searchPattern = $"{contentHash}.*.fetched.jpg";
            var matches = _fileSystem.GetFiles(_albumArtStoragePath, searchPattern);
            return matches.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error searching for cached file with hash {Hash}", contentHash);
            return null;
        }
    }

    /// <summary>
    ///     Parses light and dark color hex values from the filename pattern.
    ///     Expected pattern: {hash}.{lightHex}.{darkHex}.fetched.jpg
    /// </summary>
    private static (string? lightHex, string? darkHex) ParseColorsFromFilename(string filePath)
    {
        try
        {
            var filename = Path.GetFileNameWithoutExtension(filePath); // removes .jpg
            if (filename.EndsWith(".fetched", StringComparison.OrdinalIgnoreCase))
                filename = filename[..^8]; // remove .fetched suffix

            var parts = filename.Split('.');
            // Expected: [hash, lightHex, darkHex]
            if (parts.Length >= 3)
            {
                return (parts[^2], parts[^1]); // second-to-last and last
            }
        }
        catch
        {
            // Parsing failed, return nulls
        }
        return (null, null);
    }

    /// <summary>
    ///     Processes a new image: loads it once, extracts colors, resizes, saves with colors in filename.
    /// </summary>
    private async Task<(string? uri, string? lightSwatchId, string? darkSwatchId)> ProcessAndSaveNewImageAsync(
        string contentHash, byte[] pictureData)
    {
        // Use Lazy<Task> pattern for concurrent deduplication
        var lazyTask = _inFlightSaves.GetOrAdd(contentHash,
            _ => new Lazy<Task<string>>(() => ProcessAndSaveNewImageCoreAsync(contentHash, pictureData)));

        try
        {
            var fullPath = await lazyTask.Value.ConfigureAwait(false);
            var (lightHex, darkHex) = ParseColorsFromFilename(fullPath);
            return (fullPath, lightHex, darkHex);
        }
        finally
        {
            _inFlightSaves.TryRemove(contentHash, out _);
        }
    }

    /// <summary>
    ///     Core implementation: loads image once, extracts colors, saves with colors embedded in filename.
    /// </summary>
    private async Task<string> ProcessAndSaveNewImageCoreAsync(string contentHash, byte[] pictureData)
    {
        // Double-check: another thread may have created the file while we were waiting
        var existingFile = FindCachedFileByHash(contentHash);
        if (existingFile != null)
            return existingFile;

        // Load image ONCE
        using var image = Image.Load<Rgba32>(pictureData);

        // Extract colors from the loaded image (resize to small size for speed)
        var (lightHex, darkHex) = ExtractColorsFromLoadedImage(image);

        // Build filename with embedded colors
        var safeLight = lightHex ?? "000000";
        var safeDark = darkHex ?? "000000";
        var filename = $"{contentHash}.{safeLight}.{safeDark}.fetched.jpg";
        var fullPath = _fileSystem.Combine(_albumArtStoragePath, filename);

        // Resize for caching if needed (mutates in-place)
        if (image.Width > CachedImageMaxDimension || image.Height > CachedImageMaxDimension)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(CachedImageMaxDimension, CachedImageMaxDimension),
                Mode = ResizeMode.Max
            }));

            _logger.LogTrace("Resized album art to {Width}x{Height} for caching.", image.Width, image.Height);
        }

        // Save to disk atomically
        using var memoryStream = new MemoryStream();
        await image.SaveAsJpegAsync(memoryStream).ConfigureAwait(false);
        var imageBytes = memoryStream.ToArray();

        var tempPath = fullPath + ".tmp";
        await _fileSystem.WriteAllBytesAsync(tempPath, imageBytes).ConfigureAwait(false);

        try
        {
            _fileSystem.MoveFile(tempPath, fullPath, overwrite: false);
        }
        catch (IOException)
        {
            // Another thread won the race, clean up our temp file
            try { _fileSystem.DeleteFile(tempPath); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to clean up temp file {TempPath}", tempPath); }
        }

        return fullPath;
    }

    /// <summary>
    ///     Extracts colors from an already-loaded image by cloning and resizing to a small dimension.
    ///     This avoids decoding the image twice.
    /// </summary>
    private (string? lightHex, string? darkHex) ExtractColorsFromLoadedImage(Image<Rgba32> originalImage)
    {
        try
        {
            // Clone and resize to small dimension for fast color extraction
            using var colorImage = originalImage.Clone(x => x.Resize(new ResizeOptions
            {
                Size = new Size(ColorExtractionDimension, ColorExtractionDimension),
                Mode = ResizeMode.Max
            }));

            var pixels = new uint[colorImage.Width * colorImage.Height];
            colorImage.CopyPixelDataTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));

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

    /// <inheritdoc />
    public async Task<byte[]> ProcessImageBytesAsync(byte[] imageData, int maxDimension = 600)
    {
        if (imageData.Length == 0) return imageData;

        try
        {
            using var image = Image.Load<Rgba32>(imageData);

            // Only resize if larger than max dimension
            if (image.Width <= maxDimension && image.Height <= maxDimension)
                return imageData;

            var originalWidth = image.Width;
            var originalHeight = image.Height;

            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(maxDimension, maxDimension),
                Mode = ResizeMode.Max // Preserves aspect ratio, fits within bounds
            }));

            _logger.LogDebug("Resized image from {OrigWidth}x{OrigHeight} to {Width}x{Height}",
                originalWidth, originalHeight, image.Width, image.Height);

            using var memoryStream = new MemoryStream(imageData.Length);
            await image.SaveAsJpegAsync(memoryStream).ConfigureAwait(false);
            return memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process image, returning original bytes.");
            return imageData;
        }
    }
}
