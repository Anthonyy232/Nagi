using System.Text;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Implementation of <see cref="IPlaylistExportService" /> for M3U/M3U8 playlist format.
/// </summary>
public class M3uPlaylistExportService : IPlaylistExportService
{
    private readonly ILibraryReader _libraryReader;
    private readonly IPlaylistService _playlistService;
    private readonly ILogger<M3uPlaylistExportService> _logger;

    public M3uPlaylistExportService(
        ILibraryReader libraryReader,
        IPlaylistService playlistService,
        ILogger<M3uPlaylistExportService> logger)
    {
        _libraryReader = libraryReader;
        _playlistService = playlistService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PlaylistExportResult> ExportPlaylistAsync(Guid playlistId, string filePath)
    {
        try
        {
            var playlist = await _libraryReader.GetPlaylistByIdAsync(playlistId);
            if (playlist is null)
            {
                _logger.LogWarning("Playlist not found for export: {PlaylistId}", playlistId);
                return new PlaylistExportResult(false, 0, "Playlist not found.");
            }

            var songs = await _libraryReader.GetSongsInPlaylistOrderedAsync(playlistId);
            var songList = songs.ToList();

            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");
            sb.AppendLine($"#PLAYLIST:{playlist.Name}");

            foreach (var song in songList)
            {
                var durationSeconds = (int)song.Duration.TotalSeconds;
                var artistName = song.Artist?.Name ?? "Unknown Artist";
                var title = song.Title;

                // Extended info line: #EXTINF:{duration},{artist} - {title}
                sb.AppendLine($"#EXTINF:{durationSeconds},{artistName} - {title}");
                sb.AppendLine(song.FilePath);
            }

            // Write as UTF-8 (M3U8 format) for better international character support
            await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);

            _logger.LogInformation("Exported playlist '{PlaylistName}' with {SongCount} songs to {FilePath}",
                playlist.Name, songList.Count, filePath);

            return new PlaylistExportResult(true, songList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist {PlaylistId} to {FilePath}", playlistId, filePath);
            return new PlaylistExportResult(false, 0, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<PlaylistImportResult> ImportPlaylistAsync(string filePath, string playlistName)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("M3U file not found: {FilePath}", filePath);
                return new PlaylistImportResult(false, null, 0, 0, [], "File not found.");
            }

            var m3uDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;
            
            // Read with UTF-8 encoding by default, but handle potential BOM detection
            // Most modern M3U8 files use UTF-8, .m3u files may use system default encoding
            string[] lines;
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            if (extension == ".m3u8")
            {
                lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
            }
            else
            {
                // For .m3u files, try default encoding which handles ANSI/local codepages
                lines = await File.ReadAllLinesAsync(filePath);
            }

            var matchedSongIds = new List<Guid>();
            var unmatchedPaths = new List<string>();

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Skip empty lines and M3U directives
                if (string.IsNullOrEmpty(trimmedLine) ||
                    trimmedLine.StartsWith('#'))
                {
                    continue;
                }

                // This line should be a file path
                var songPath = trimmedLine;

                // Resolve relative paths against the M3U file's directory
                if (!Path.IsPathRooted(songPath))
                {
                    songPath = Path.GetFullPath(Path.Combine(m3uDirectory, songPath));
                }

                // Normalize the path for matching
                songPath = Path.GetFullPath(songPath);

                var song = await _libraryReader.GetSongByFilePathAsync(songPath);
                if (song is not null)
                {
                    matchedSongIds.Add(song.Id);
                }
                else
                {
                    unmatchedPaths.Add(trimmedLine);
                    _logger.LogDebug("Could not match path to library: {Path}", songPath);
                }
            }

            if (matchedSongIds.Count == 0)
            {
                _logger.LogWarning("No songs matched during import of {FilePath}", filePath);
                return new PlaylistImportResult(false, null, 0, unmatchedPaths.Count, unmatchedPaths,
                    "No songs from the playlist were found in your library.");
            }

            // Create the playlist with matched songs
            var playlist = await _playlistService.CreatePlaylistAsync(playlistName);
            if (playlist is null)
            {
                _logger.LogError("Failed to create playlist during import");
                return new PlaylistImportResult(false, null, matchedSongIds.Count, unmatchedPaths.Count,
                    unmatchedPaths, "Failed to create playlist.");
            }

            await _playlistService.AddSongsToPlaylistAsync(playlist.Id, matchedSongIds);

            _logger.LogInformation(
                "Imported playlist '{PlaylistName}' from {FilePath}: {Matched} matched, {Unmatched} unmatched",
                playlistName, filePath, matchedSongIds.Count, unmatchedPaths.Count);

            return new PlaylistImportResult(true, playlist.Id, matchedSongIds.Count, unmatchedPaths.Count,
                unmatchedPaths);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import playlist from {FilePath}", filePath);
            return new PlaylistImportResult(false, null, 0, 0, [], ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<BatchExportResult> ExportAllPlaylistsAsync(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var playlists = await _libraryReader.GetAllPlaylistsAsync();
            var playlistList = playlists.ToList();

            if (playlistList.Count == 0)
            {
                return new BatchExportResult(false, 0, 0, "No playlists to export.");
            }

            var exportedCount = 0;
            var totalSongs = 0;

            foreach (var playlist in playlistList)
            {
                // Get songs to check if playlist is empty
                var songs = await _libraryReader.GetSongsInPlaylistOrderedAsync(playlist.Id);
                var songCount = songs.Count();
                
                // Skip empty playlists
                if (songCount == 0)
                {
                    _logger.LogDebug("Skipping empty playlist '{PlaylistName}'", playlist.Name);
                    continue;
                }

                // Sanitize playlist name for use as filename
                var safeName = SanitizeFileName(playlist.Name);
                var filePath = Path.Combine(directoryPath, $"{safeName}.m3u8");

                var result = await ExportPlaylistAsync(playlist.Id, filePath);
                if (result.Success)
                {
                    exportedCount++;
                    totalSongs += result.SongCount;
                }
            }

            _logger.LogInformation("Batch exported {Count} playlists with {Songs} total songs to {Directory}",
                exportedCount, totalSongs, directoryPath);

            return new BatchExportResult(exportedCount > 0, exportedCount, totalSongs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to batch export playlists to {DirectoryPath}", directoryPath);
            return new BatchExportResult(false, 0, 0, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<BatchImportResult> ImportMultiplePlaylistsAsync(IEnumerable<string> filePaths)
    {
        var pathList = filePaths.ToList();
        var importedCount = 0;
        var totalMatched = 0;
        var totalUnmatched = 0;
        var failedFiles = new List<string>();

        foreach (var filePath in pathList)
        {
            var playlistName = Path.GetFileNameWithoutExtension(filePath);
            var result = await ImportPlaylistAsync(filePath, playlistName);

            if (result.Success)
            {
                importedCount++;
                totalMatched += result.MatchedSongs;
                totalUnmatched += result.UnmatchedSongs;
            }
            else
            {
                failedFiles.Add(Path.GetFileName(filePath));
            }
        }

        _logger.LogInformation(
            "Batch imported {Count} playlists: {Matched} matched, {Unmatched} unmatched, {Failed} failed",
            importedCount, totalMatched, totalUnmatched, failedFiles.Count);

        return new BatchImportResult(importedCount > 0, importedCount, totalMatched, totalUnmatched, failedFiles);
    }

    /// <summary>
    ///     Sanitizes a string for use as a file name by removing invalid characters.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "playlist" : sanitized;
    }
}
