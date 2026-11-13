using System.Globalization;

namespace Nagi.Core.Artwork;

public static class ArtworkUri
{
    /// <summary>
    ///     The image at <paramref name="path" /> as a URI that changes with <paramref name="modifiedUtc" />. Artist and
    ///     playlist images are replaced in place, and image caches key on the URI, so a new stamp makes them load the
    ///     new image.
    /// </summary>
    /// <returns><paramref name="path" /> unchanged when there is no stamp, and <c>null</c> for no image.</returns>
    public static string? Versioned(string? path, DateTime? modifiedUtc)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (modifiedUtc is not { } modified) return path;

        var version = "v=" + modified.Ticks.ToString(CultureInfo.InvariantCulture);
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)) return $"{path}?{version}";
        return uri.Query.Length > 0
            ? $"{uri.GetLeftPart(UriPartial.Query)}&{version}{uri.Fragment}"
            : $"{uri.GetLeftPart(UriPartial.Path)}?{version}{uri.Fragment}";
    }
}
