using System.Collections.Concurrent;
using System.Security.Cryptography;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;
using MaterialColorUtilities.Utils;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
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

    private const int MaxImageSharpActiveAllocationMegabytes = 128;
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);

    private readonly string _albumArtStoragePath;
    private readonly IFileSystemService _fileSystem;
    private readonly ILogger<ImageSharpProcessor> _logger;

    /// <summary>
    ///     Tracks in-flight save operations to prevent race conditions when multiple threads
    ///     try to save the same image content simultaneously. Each key is a content hash,
    ///     and the value is a lazy task that ensures only one save operation runs per hash.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlightSaves = new();

    static ImageSharpProcessor()
    {
        Configuration.Default.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            MaximumPoolSizeMegabytes = 32,
            AccumulativeAllocationLimitMegabytes = MaxImageSharpActiveAllocationMegabytes
        });
    }

    public ImageSharpProcessor(IPathConfiguration pathConfig, IFileSystemService fileSystem,
        ILogger<ImageSharpProcessor> logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
        _albumArtStoragePath = pathConfig.AlbumArtCachePath;
    }

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
            var contentHash = Convert.ToHexStringLower(SHA256.HashData(pictureData));

            var existingFile = FindCachedFileByHash(contentHash);
            if (existingFile != null)
            {
                var (lightHex, darkHex) = ParseColorsFromFilename(existingFile);
                return (existingFile, lightHex, darkHex);
            }

            return await ProcessAndSaveNewImageAsync(contentHash, pictureData).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save cover art and extract colors.");
            return (null, null, null);
        }
    }

    private string? FindCachedFileByHash(string contentHash)
    {
        try
        {
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
        var filename = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(filePath));
        var parts = filename.Split('.');
        return parts.Length >= 3 ? (parts[^2], parts[^1]) : (null, null);
    }

    private async Task<(string? uri, string? lightSwatchId, string? darkSwatchId)> ProcessAndSaveNewImageAsync(
        string contentHash, byte[] pictureData)
    {
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
            _inFlightSaves.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(contentHash, lazyTask));
        }
    }

    private async Task<string> ProcessAndSaveNewImageCoreAsync(string contentHash, byte[] pictureData)
    {
        // Double-check: another thread may have created the file while we were waiting
        var existingFile = FindCachedFileByHash(contentHash);
        if (existingFile != null)
            return existingFile;

        // Decode directly toward the cached size. ImageSharp v4 can apply this during decode for
        // supported formats, avoiding large transient buffers for oversized embedded art.
        using var image = await LoadImageAsync(pictureData, CachedImageMaxDimension).ConfigureAwait(false);

        var (lightHex, darkHex) = ExtractColorsFromLoadedImage(image);

        var safeLight = lightHex ?? "000000";
        var safeDark = darkHex ?? "000000";
        var baseFileName = $"{contentHash}.{safeLight}.{safeDark}";

        if (image.Width > CachedImageMaxDimension || image.Height > CachedImageMaxDimension)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(CachedImageMaxDimension, CachedImageMaxDimension),
                Mode = ResizeMode.Max
            }));

            _logger.LogTrace("Resized album art to {Width}x{Height} for caching.", image.Width, image.Height);
        }

        using var memoryStream = new MemoryStream();
        await image.SaveAsJpegAsync(memoryStream).ConfigureAwait(false);
        return (await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, _albumArtStoragePath,
            baseFileName, ".fetched", memoryStream.ToArray()).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Extracts colors from an already-loaded image by cloning and resizing to a small dimension.
    ///     This avoids decoding the image twice.
    /// </summary>
    private (string? lightHex, string? darkHex) ExtractColorsFromLoadedImage(Image<Bgra32> originalImage)
    {
        try
        {
            using var colorImage = originalImage.Clone(x => x.Resize(new ResizeOptions
            {
                Size = new Size(ColorExtractionDimension, ColorExtractionDimension),
                Mode = ResizeMode.Max
            }));

            var pixels = new uint[colorImage.Width * colorImage.Height];
            // Read as little-endian integers, BGRA bytes are MaterialColorUtilities' ARGB values.
            colorImage.CopyPixelDataTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));

            var seedColor = ImageUtils.ColorsFromImage(pixels).FirstOrDefault();
            if (seedColor == default)
            {
                _logger.LogWarning("Could not determine a seed color from the image.");
                return (null, null);
            }

            var corePalette = CorePalette.ContentOf(seedColor);
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

    public async Task<byte[]> ProcessImageBytesAsync(byte[] imageData, int maxDimension = 600)
    {
        if (imageData.Length == 0 || maxDimension <= 0) return imageData;

        try
        {
            var imageInfo = Image.Identify(CreateDecoderOptions(), imageData);
            if (imageInfo.Width <= maxDimension && imageInfo.Height <= maxDimension)
                return imageData;

            using var image = await LoadImageAsync(imageData, maxDimension).ConfigureAwait(false);

            // TargetSize is a decode hint. Keep this guard for formats that still decode full-size.
            if (image.Width > maxDimension || image.Height > maxDimension)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(maxDimension, maxDimension),
                    Mode = ResizeMode.Max
                }));
            }

            _logger.LogDebug("Resized image from {OrigWidth}x{OrigHeight} to {Width}x{Height}",
                imageInfo.Width, imageInfo.Height, image.Width, image.Height);

            using var memoryStream = new MemoryStream();
            await image.SaveAsJpegAsync(memoryStream).ConfigureAwait(false);
            return memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process image, returning original bytes.");
            return imageData;
        }
    }

    private static async Task<Image<Bgra32>> LoadImageAsync(byte[] imageData, int maxDimension)
    {
        // PNG decoding can allocate a full-size frame before resizing within the shared memory limit.
        await DecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return Image.Load<Bgra32>(CreateDecoderOptions(maxDimension), imageData);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static DecoderOptions CreateDecoderOptions(int? maxDimension = null)
    {
        return new DecoderOptions
        {
            ColorProfileHandling = ColorProfileHandling.Convert,
            MaxFrames = 1,
            TargetSize = maxDimension.HasValue ? new Size(maxDimension.Value, maxDimension.Value) : null
        };
    }
}
