using System;
using System.Globalization;
using System.IO;
using System.Web;
using Microsoft.Extensions.Caching.Memory;

namespace Nagi.Presentation.Helpers;

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
}
