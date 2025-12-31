using System;
using System.IO;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Provides utility methods for formatting image URIs and safe ImageSource creation.
/// </summary>
public static class ImageUriHelper
{
    private const string CacheBusterPrefix = "?v=";

    /// <summary>
    ///     Appends a cache-busting query parameter to a local file URI based on its last write time.
    ///     This forces UI components like ImageEx to refresh when the file on disk changes.
    /// </summary>
    /// <remarks>
    ///     This method only performs a disk read for rooted, physical file paths (e.g., C:\... or \\server\...).
    ///     For non-physical URIs (ms-appx, http, etc.), the original path is returned unchanged.
    /// </remarks>
    /// <param name="path">The local file path or URI.</param>
    /// <returns>A URI string with a cache-buster query parameter if it's a local file; otherwise, the original path.</returns>
    public static string? GetUriWithCacheBuster(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (HasCacheBuster(path)) return path;

        try
        {
            // Only perform disk I/O for rooted physical paths.
            if (IsRootedPhysicalPath(path))
            {
                // Check if file exists first to prevent "path not found" errors
                // when ImageEx tries to load non-existent files
                if (!File.Exists(path))
                {
                    return null;
                }

                var lastWriteTime = File.GetLastWriteTimeUtc(path);

                // File.GetLastWriteTimeUtc returns 1601-01-01 00:00:00 UTC if the file doesn't exist.
                if (lastWriteTime.Year > 1601)
                {
                    return BuildCacheBustedUri(path, lastWriteTime.Ticks);
                }
            }
        }
        catch
        {
            // Return null on any error to prevent ImageEx from trying to load invalid paths
            return null;
        }

        return path;
    }

    /// <summary>
    ///     Appends a cache-busting query parameter to a path using a provided modification date.
    ///     This is highly efficient for large lists as it avoids disk I/O.
    /// </summary>
    /// <param name="path">The file path or URI.</param>
    /// <param name="modifiedDate">The modification date to use for the version.</param>
    /// <returns>A URI string with a cache-buster query parameter.</returns>
    public static string? GetUriWithCacheBuster(string? path, DateTime? modifiedDate)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!modifiedDate.HasValue) return path;
        if (HasCacheBuster(path)) return path;

        try
        {
            // For physical file paths, verify the file exists to prevent ImageEx load errors
            if (IsRootedPhysicalPath(path) && !File.Exists(path))
            {
                return null;
            }

            return BuildCacheBustedUri(path, modifiedDate.Value.Ticks);
        }
        catch
        {
            // Return null on any error to prevent ImageEx from trying to load invalid paths
            return null;
        }
    }

    /// <summary>
    ///     Checks if a path already has a cache-buster query parameter.
    /// </summary>
    private static bool HasCacheBuster(string path)
    {
        // Check if the path has a query parameter that looks like our cache buster.
        // This is more robust than a simple Contains check.
        var queryIndex = path.LastIndexOf('?');
        return queryIndex >= 0 && path.IndexOf("v=", queryIndex, StringComparison.OrdinalIgnoreCase) > queryIndex;
    }

    /// <summary>
    ///     Checks if a path is a rooted physical path (drive letter or UNC).
    /// </summary>
    private static bool IsRootedPhysicalPath(string path)
    {
        // Check for standard drive letter paths (e.g., C:\...) or UNC paths (e.g., \\server\...)
        return path.Length >= 3 &&
               ((path[1] == ':' && (path[2] == '\\' || path[2] == '/')) ||
                path.StartsWith(@"\\", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Builds the final URI string with the cache-buster appended.
    /// </summary>
    private static string BuildCacheBustedUri(string path, long ticks)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            return string.Concat(uri.AbsoluteUri, CacheBusterPrefix, ticks.ToString());
        }

        // If it's not a valid absolute URI, just append directly to the path.
        return string.Concat(path, CacheBusterPrefix, ticks.ToString());
    }

    /// <summary>
    ///     Safely converts a URI string to an ImageSource (BitmapImage).
    ///     Returns null if the input is null, empty, or an invalid URI.
    /// </summary>
    /// <param name="uriString">The URI string to convert.</param>
    /// <returns>A BitmapImage if successful; otherwise, null.</returns>
    public static ImageSource? SafeGetImageSource(string? uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString))
            return null;

        try
        {
            if (Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            {
                return new BitmapImage(uri);
            }

            // Handle cases where it might be a local path that Uri.TryCreate didn't catch
            // as absolute but we still want to try to load it.
            return new BitmapImage(new Uri(uriString));
        }
        catch
        {
            // Catch all to prevent crashes during state transitions or for invalid paths
            return null;
        }
    }
}
