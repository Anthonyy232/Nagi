namespace Nagi.Core.Helpers;

public interface IPathConfiguration
{
    string AppDataRoot { get; }

    string SettingsFilePath { get; }

    string PlaybackStateFilePath { get; }

    string AlbumArtCachePath { get; }

    string ArtistImageCachePath { get; }

    string PlaylistImageCachePath { get; }

    string LrcCachePath { get; }

    string RomanizationPacksPath { get; }

    string DatabasePath { get; }

    string LogsDirectory { get; }
}
