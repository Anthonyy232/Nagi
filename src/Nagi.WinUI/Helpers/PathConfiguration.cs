using System;
using System.IO;
using Windows.ApplicationModel;
using Windows.Storage;
using Microsoft.Extensions.Configuration;
using Nagi.Core.Helpers;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Provides a centralized source of truth for all application data paths.
/// </summary>
public class PathConfiguration : IPathConfiguration
{
    public PathConfiguration(IConfiguration configuration)
    {
        try
        {
            // Windows App Runtime local folder is the correct way to handle file storage in packaged apps.
            AppDataRoot = ApplicationData.Current.LocalFolder.Path;
        }
        catch (Exception)
        {
            // Fallback for environments where ApplicationData is not initialized (e.g., unit tests)
            AppDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nagi");
        }

        // Define all other paths based on the determined root.
        SettingsFilePath = Path.Combine(AppDataRoot, "settings.json");
        PlaybackStateFilePath = Path.Combine(AppDataRoot, "playback_state.json");
        AlbumArtCachePath = Path.Combine(AppDataRoot, "AlbumArt");
        ArtistImageCachePath = Path.Combine(AppDataRoot, "ArtistImages");
        PlaylistImageCachePath = Path.Combine(AppDataRoot, "PlaylistImages");
        LrcCachePath = Path.Combine(AppDataRoot, "LrcCache");
        DatabasePath = Path.Combine(AppDataRoot, "nagi.db");
        LogsDirectory = Path.Combine(AppDataRoot, "Logs");

        // Ensure all necessary directories exist on startup.
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(AlbumArtCachePath);
        Directory.CreateDirectory(ArtistImageCachePath);
        Directory.CreateDirectory(PlaylistImageCachePath);
        Directory.CreateDirectory(LrcCachePath);
        Directory.CreateDirectory(LogsDirectory);
    }

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
    public string PlaylistImageCachePath { get; }

    /// <inheritdoc />
    public string LrcCachePath { get; }

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <inheritdoc />
    public string LogsDirectory { get; }
}
