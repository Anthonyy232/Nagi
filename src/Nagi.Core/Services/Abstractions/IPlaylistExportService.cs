using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface IPlaylistExportService
{
    /// <summary>
    ///     Imports a playlist from an M3U/M3U8 file.
    /// </summary>
    /// <returns>A result containing the new playlist ID and match statistics.</returns>
    Task<PlaylistImportResult> ImportPlaylistAsync(string filePath, string playlistName);

    /// <summary>
    ///     Exports all playlists to a directory, creating one M3U8 file per playlist.
    /// </summary>
    /// <returns>A result indicating success and the number of playlists exported.</returns>
    Task<BatchExportResult> ExportAllPlaylistsAsync(string directoryPath);

    /// <summary>
    ///     Imports multiple M3U/M3U8 files as playlists.
    /// </summary>
    /// <returns>A result containing import statistics for all files.</returns>
    Task<BatchImportResult> ImportMultiplePlaylistsAsync(IEnumerable<string> filePaths);
}
