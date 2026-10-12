using System.Collections.Frozen;

namespace Nagi.Core.Constants;

public static class FileExtensions
{
    public static readonly HashSet<string> MusicFileExtensions = new(new[]
    {
        ".aa", ".aac", ".aiff", ".ape", ".dsf", ".flac",
        ".m4a", ".m4b", ".mp3", ".mpc", ".mpp", ".ogg",
        ".oga", ".opus", ".wav", ".wma", ".wv", ".webm",
        ".asf", ".mp4", ".m4v",
        ".mpeg", ".mpg", ".mpe"
    }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Supported image file extensions for cover art (WinUI 3 BitmapImage compatible).
    /// </summary>
    public static readonly HashSet<string> ImageFileExtensions = new(new[]
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".ico"
    }, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, int> CoverArtPriorities =
        new Dictionary<string, int>
        {
            ["cover"] = 0,
            ["folder"] = 1,
            ["front"] = 2,
            ["album"] = 3
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public const string ArtistImageFileName = "artist";

    /// <summary>Returns a lower value for preferred cover names, or int.MaxValue for other names.</summary>
    public static int GetCoverArtPriority(string fileNameWithoutExt) =>
        CoverArtPriorities.GetValueOrDefault(fileNameWithoutExt, int.MaxValue);
}
