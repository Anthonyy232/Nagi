namespace Nagi.Core.Platform;

/// <summary>
///     Lays out the app's files under <see cref="AppDataRoot" />, which each platform chooses, and creates the folders.
/// </summary>
public class PathConfiguration : IPathConfiguration
{
    public PathConfiguration(string appDataRoot)
    {
        AppDataRoot = appDataRoot;
        PlaybackStateFilePath = Path.Combine(AppDataRoot, "playback_state.json");
        AlbumArtCachePath = Path.Combine(AppDataRoot, "AlbumArt");
        ArtistImageCachePath = Path.Combine(AppDataRoot, "ArtistImages");
        PlaylistImageCachePath = Path.Combine(AppDataRoot, "PlaylistImages");
        LrcCachePath = Path.Combine(AppDataRoot, "LrcCache");
        RomanizationPacksPath = Path.Combine(AppDataRoot, "RomanizationPacks");
        DatabasePath = Path.Combine(AppDataRoot, "nagi.db");
        LogsDirectory = Path.Combine(AppDataRoot, "Logs");
        LibraryCacheFolders = [AlbumArtCachePath, ArtistImageCachePath, PlaylistImageCachePath, LrcCachePath];

        foreach (var folder in LibraryCacheFolders.Append(RomanizationPacksPath).Append(LogsDirectory))
            Directory.CreateDirectory(folder);
    }

    public string AppDataRoot { get; }

    public string PlaybackStateFilePath { get; }

    public string AlbumArtCachePath { get; }

    public string ArtistImageCachePath { get; }

    public string PlaylistImageCachePath { get; }

    public string LrcCachePath { get; }

    public IReadOnlyList<string> LibraryCacheFolders { get; }

    public string RomanizationPacksPath { get; }

    public string DatabasePath { get; }

    public string LogsDirectory { get; }
}
