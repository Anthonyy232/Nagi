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
public class PathConfiguration : IPathConfiguration
{
    public PathConfiguration()
    {
        IsPackaged = IsRunningInPackage();

        AppDataRoot = IsPackaged
            ? ApplicationData.Current.LocalFolder.Path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nagi");

        // Define all other paths based on the determined root.
        SettingsFilePath = Path.Combine(AppDataRoot, "settings.json");
        PlaybackStateFilePath = Path.Combine(AppDataRoot, "playback_state.json");
        AlbumArtCachePath = Path.Combine(AppDataRoot, "AlbumArt");
        ArtistImageCachePath = Path.Combine(AppDataRoot, "ArtistImages");
        LrcCachePath = Path.Combine(AppDataRoot, "LrcCache");
        DatabasePath = Path.Combine(AppDataRoot, "nagi.db");

        // Ensure all necessary directories exist on startup.
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(AlbumArtCachePath);
        Directory.CreateDirectory(ArtistImageCachePath);
        Directory.CreateDirectory(LrcCachePath);
    }

    /// <inheritdoc />
    public bool IsPackaged { get; }

    /// <inheritdoc />
    public string AppDataRoot { get; }

    /// <inheritdoc />
    public string SettingsFilePath { get; }

    /// <inheritdoc />
    public string PlaybackStateFilePath { get; }

    /// <inheritdoc />
    public string AlbumArtCachePath { get; }

    /// <inheritdoc />
    public string ArtistImageCachePath { get; }

    /// <inheritdoc />
    public string LrcCachePath { get; }

    /// <inheritdoc />
    public string DatabasePath { get; }

    private static bool IsRunningInPackage()
    {
        try
        {
            // If this property can be accessed without throwing, we are in a package.
            return Package.Current != null;
        }
        catch
        {
            // An exception will be thrown if we are not in a package.
            return false;
        }
    }
}