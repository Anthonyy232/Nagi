namespace Nagi.Core.Platform;

public interface IPathConfiguration
{
    string AppDataRoot { get; }

    string PlaybackStateFilePath { get; }

    string AlbumArtCachePath { get; }

    string ArtistImageCachePath { get; }

    string PlaylistImageCachePath { get; }

    string LrcCachePath { get; }

    /// <summary>
    ///     The library's cached images and lyrics, which belong with the database in a backup and a reset.
    /// </summary>
    IReadOnlyList<string> LibraryCacheFolders { get; }

    string RomanizationPacksPath { get; }

    string DatabasePath { get; }

    string LogsDirectory { get; }
}
