using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines the contract for exporting and importing playlists to/from external file formats.
/// </summary>
public interface IPlaylistExportService
{
    /// <summary>
    ///     Exports a playlist to an M3U/M3U8 file.
    /// </summary>
    /// <param name="playlistId">The ID of the playlist to export.</param>
    /// <param name="filePath">The destination file path.</param>
    /// <returns>A result indicating success and the number of songs exported.</returns>
    Task<PlaylistExportResult> ExportPlaylistAsync(Guid playlistId, string filePath);

    /// <summary>
    ///     Imports a playlist from an M3U/M3U8 file.
    /// </summary>
    /// <param name="filePath">The path to the M3U file to import.</param>
    /// <param name="playlistName">The name for the new playlist.</param>
    /// <returns>A result containing the new playlist ID and match statistics.</returns>
    Task<PlaylistImportResult> ImportPlaylistAsync(string filePath, string playlistName);

    /// <summary>
    ///     Exports all playlists to a directory, creating one M3U8 file per playlist.
    /// </summary>
    /// <param name="directoryPath">The destination directory path.</param>
    /// <returns>A result indicating success and the number of playlists exported.</returns>
    Task<BatchExportResult> ExportAllPlaylistsAsync(string directoryPath);

    /// <summary>
    ///     Imports multiple M3U/M3U8 files as playlists.
    /// </summary>
    /// <param name="filePaths">The paths to the M3U files to import.</param>
    /// <returns>A result containing import statistics for all files.</returns>
    Task<BatchImportResult> ImportMultiplePlaylistsAsync(IEnumerable<string> filePaths);
}
