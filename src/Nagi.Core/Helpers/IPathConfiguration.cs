namespace Nagi.Core.Helpers;

/// <summary>
/// Defines the contract for a centralized source of application data paths.
/// </summary>
public interface IPathConfiguration {
    /// <summary>
    /// Gets a value indicating whether the application is running in a packaged context (e.g., MSIX).
    /// </summary>
    bool IsPackaged { get; }

    /// <summary>
    /// Gets the root directory for all application data.
    /// </summary>
    string AppDataRoot { get; }

    /// <summary>
    /// Gets the full path to the settings.json file.
    /// </summary>
    string SettingsFilePath { get; }

    /// <summary>
    /// Gets the full path to the playback_state.json file.
    /// </summary>
    string PlaybackStateFilePath { get; }

    /// <summary>
    /// Gets the full path to the directory for caching album art images.
    /// </summary>
    string AlbumArtCachePath { get; }

    /// <summary>
    /// Gets the full path to the directory for caching artist images.
    /// </summary>
    string ArtistImageCachePath { get; }

    /// <summary>
    /// Gets the full path to the SQLite database file.
    /// </summary>
    string DatabasePath { get; }
}