using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface IPlaylistExportService
{
    /// <summary>
    ///     Writes one M3U8 file per playlist, skipping playlists with no songs on this PC.
    /// </summary>
    Task<BatchExportResult> ExportAllPlaylistsAsync(string directoryPath);

    /// <summary>
    ///     Imports each M3U/M3U8 file as a new playlist named after the file.
    /// </summary>
    Task<BatchImportResult> ImportMultiplePlaylistsAsync(IEnumerable<string> filePaths);
}
