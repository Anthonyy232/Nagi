using System;
using System.IO;
using Windows.ApplicationModel;
using Windows.Storage;
using Nagi.Core.Helpers;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Provides a centralized source of truth for all application data paths,
///     automatically adapting to whether the app is running in a packaged or unpackaged context.
/// </summary>
public class PathConfiguration : IPathConfiguration {
    /// <summary>
    ///     Gets a value indicating whether the application is running in a packaged context (e.g., MSIX).
    /// </summary>
    public bool IsPackaged { get; }

    /// <summary>
    ///     Gets the root directory for all application data.
    ///     - Packaged: %LOCALAPPDATA%\Packages\[PackageName]\LocalState
    ///     - Unpackaged: %LOCALAPPDATA%\Nagi
    /// </summary>
    public string AppDataRoot { get; }

    /// <summary>
    ///     Gets the full path to the settings.json file.
    /// </summary>
    public string SettingsFilePath { get; }

    /// <summary>
    ///     Gets the full path to the playback_state.json file.
    /// </summary>
    public string PlaybackStateFilePath { get; }

    /// <summary>
    ///     Gets the full path to the directory for caching album art images.
    /// </summary>
    public string AlbumArtCachePath { get; }

    /// <summary>
    ///     Gets the full path to the directory for caching artist images.
    /// </summary>
    public string ArtistImageCachePath { get; }

    /// <summary>
    ///     Gets the full path to the SQLite database file.
    /// </summary>
    public string DatabasePath { get; }

    public PathConfiguration() {
        IsPackaged = IsRunningInPackage();

        AppDataRoot = IsPackaged
            ? ApplicationData.Current.LocalFolder.Path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nagi");

        // Define all other paths based on the determined root.
        SettingsFilePath = Path.Combine(AppDataRoot, "settings.json");
        PlaybackStateFilePath = Path.Combine(AppDataRoot, "playback_state.json");
        AlbumArtCachePath = Path.Combine(AppDataRoot, "AlbumArt");
        ArtistImageCachePath = Path.Combine(AppDataRoot, "ArtistImages");
        DatabasePath = Path.Combine(AppDataRoot, "nagi.db");

        // Ensure all necessary directories exist on startup.
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(AlbumArtCachePath);
        Directory.CreateDirectory(ArtistImageCachePath);
    }

    private static bool IsRunningInPackage() {
        try {
            // If this property can be accessed without throwing, we are in a package.
            return Package.Current != null;
        }
        catch {
            // An exception will be thrown if we are not in a package.
            return false;
        }
    }
}