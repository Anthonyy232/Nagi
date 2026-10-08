using System;
using System.Globalization;
using System.IO;
using System.Web;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Provides utility methods for formatting image URIs and safe ImageSource creation.
/// </summary>
public static class ImageUriHelper
{
    // Shared artwork appears repeatedly in song lists; avoid repeating disk reads for each row.
    private static readonly MemoryCache _uriCache = new(new MemoryCacheOptions { SizeLimit = 2048 });
    private static readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Appends a cache-busting query parameter to a local file URI based on its last write time.
    ///     This forces UI components like ImageEx to refresh when the file on disk changes.
    /// </summary>
    /// <remarks>
    ///     This method only performs a disk read for rooted, physical file paths (e.g., C:\... or \\server\...).
    ///     For non-physical URIs (ms-appx, http, etc.), the original path is returned unchanged.
    /// </remarks>
    /// <returns>A URI string with a cache-buster query parameter if it's a local file; otherwise, the original path.</returns>
    public static string? GetUriWithCacheBuster(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (HasCacheBuster(path)) return path;

        // Only cache rooted physical paths — non-physical URIs skip disk I/O anyway.
        if (Path.IsPathFullyQualified(path))
        {
            if (_uriCache.TryGetValue(path, out string? cached))
                return cached;

            string? result;
            try
            {
                if (!File.Exists(path))
                {
                    result = null;
                }
                else
                {
                    var lastWriteTime = File.GetLastWriteTimeUtc(path);
                    result = lastWriteTime.Year > 1601 ? BuildCacheBustedUri(path, lastWriteTime.Ticks) : null;
                }
            }
            catch
            {
                result = null;
            }

            _uriCache.Set(path, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _cacheLifetime,
                Size = 1
            });
            return result;
        }

        return path;
    }

    /// <summary>
    ///     Appends a cache-busting query parameter to a path using a provided modification date.
    ///     Avoids querying the timestamp; local file existence is still checked.
    /// </summary>
    /// <returns>A URI string with a cache-buster query parameter.</returns>
    public static string? GetUriWithCacheBuster(string? path, DateTime? modifiedDate)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!modifiedDate.HasValue) return path;
        if (HasCacheBuster(path)) return path;

        try
        {
            // For physical file paths, verify the file exists to prevent ImageEx load errors
            if (Path.IsPathFullyQualified(path) && !File.Exists(path))
            {
                return null;
            }

            return BuildCacheBustedUri(path, modifiedDate.Value.Ticks);
        }
        catch
        {
            return null;
        }
    }

    private static bool HasCacheBuster(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
        HttpUtility.ParseQueryString(uri.Query)["v"] is not null;

    private static string BuildCacheBustedUri(string path, long ticks)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            query["v"] = ticks.ToString(CultureInfo.InvariantCulture);
            return $"{uri.GetLeftPart(UriPartial.Path)}?{query}{uri.Fragment}";
        }

        // If it's not a valid absolute URI, just append directly to the path.
        return string.Concat(path, "?v=", ticks.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     Safely converts a URI string to an ImageSource (BitmapImage).
    ///     Returns null if the input is null, empty, or an invalid URI.
    /// </summary>
    /// <returns>A BitmapImage if successful; otherwise, null.</returns>
    public static ImageSource? SafeGetImageSource(string? uriString)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            return null;

        try
        {
            return new BitmapImage(uri);
        }
        catch
        {
            // Catch all to prevent crashes during state transitions or for invalid paths
            return null;
        }
    }

    /// <summary>
    ///     Creates a BitmapImage with an explicit logical decode width. The decode hint must
    ///     be set BEFORE the URI source so the framework decodes at the requested resolution
    ///     instead of the source's native size — using new BitmapImage(uri) starts decoding
    ///     immediately and ignores any subsequent DecodePixelWidth assignment.
    ///     The framework multiplies the logical width by the active DPI scale.
    /// </summary>
    /// <returns>A configured BitmapImage, or null on invalid input.</returns>
    public static ImageSource? GetDecodedBitmap(string? uriString, int decodePixelWidth)
    {
        if (decodePixelWidth <= 0 || !Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            return null;

        try
        {
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = decodePixelWidth,
            };
            bitmap.UriSource = uri;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
