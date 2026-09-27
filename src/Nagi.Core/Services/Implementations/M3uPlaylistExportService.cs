using System.Text;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
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
    public async Task<PlaylistImportResult> ImportPlaylistAsync(string filePath, string playlistName)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("M3U file not found: {FilePath}", filePath);
                return new PlaylistImportResult(false, null, 0, 0, [], string.Format(Resources.Strings.Format_NotFound, Resources.Strings.Label_File));
            }

            var m3uDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;

            var matchedSongIds = new List<Guid>();
            var unmatchedPaths = new List<string>();

            await foreach (var line in File.ReadLinesAsync(filePath, Encoding.UTF8).ConfigureAwait(false))
            {
                var trimmedLine = line.Trim();

                if (string.IsNullOrEmpty(trimmedLine) ||
                    trimmedLine.StartsWith('#'))
                {
                    continue;
                }

                var songPath = ResolveSongPath(trimmedLine, m3uDirectory);
                if (songPath is null)
                {
                    unmatchedPaths.Add(trimmedLine);
                    continue;
                }

                var song = await _libraryReader.GetSongByFilePathAsync(songPath).ConfigureAwait(false);
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

            matchedSongIds = matchedSongIds.Distinct().ToList();
            if (matchedSongIds.Count == 0)
            {
                _logger.LogWarning("No songs matched during import of {FilePath}", filePath);
                return new PlaylistImportResult(false, null, 0, unmatchedPaths.Count, unmatchedPaths,
                    Resources.Strings.Error_M3uImportNoSongsMatched);
            }

            var playlist = await _playlistService.CreatePlaylistAsync(playlistName).ConfigureAwait(false);
            if (playlist is null)
            {
                _logger.LogError("Failed to create playlist during import");
                return new PlaylistImportResult(false, null, matchedSongIds.Count, unmatchedPaths.Count,
                    unmatchedPaths, string.Format(Resources.Strings.Format_NotFound, Resources.Strings.Label_Playlist));
            }

            var songsAdded = false;
            try
            {
                songsAdded = await _playlistService.AddSongsToPlaylistAsync(playlist.Id, matchedSongIds).ConfigureAwait(false);
            }
            finally
            {
                if (!songsAdded) await _playlistService.DeletePlaylistAsync(playlist.Id).ConfigureAwait(false);
            }

            if (!songsAdded)
            {
                _logger.LogWarning("Failed to add imported songs to playlist {PlaylistId}", playlist.Id);
                return new PlaylistImportResult(false, null, matchedSongIds.Count, unmatchedPaths.Count,
                    unmatchedPaths, Resources.Strings.Error_M3uImportAddSongsFailed);
            }

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

    private string? ResolveSongPath(string entry, string playlistDirectory)
    {
        try
        {
            // Decode only explicit URIs. Literal file paths may contain '%' or '#'.
            if (!Path.IsPathRooted(entry) && entry.Contains(':'))
            {
                if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri) || !uri.IsFile)
                    return null;

                entry = uri.LocalPath;
                if (!Path.IsPathFullyQualified(entry) || string.IsNullOrEmpty(Path.GetFileName(entry)))
                    return null;
            }

            return Path.GetFullPath(entry, playlistDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.LogDebug(ex, "Skipping invalid playlist entry {Path}", entry);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<BatchExportResult> ExportAllPlaylistsAsync(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);

            var playlists = await _libraryReader.GetAllPlaylistsAsync().ConfigureAwait(false);
            var playlistList = playlists.ToList();

            if (playlistList.Count == 0)
            {
                return new BatchExportResult(false, 0, 0, Resources.Strings.Error_NoPlaylistsToExport);
            }

            var exportedCount = 0;
            var totalSongs = 0;
            var reservedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var playlist in playlistList)
            {
                var songs = (await _libraryReader.GetSongsInPlaylistOrderedAsync(playlist.Id).ConfigureAwait(false))
                    .ToList();

                if (songs.Count == 0)
                {
                    _logger.LogDebug("Skipping empty playlist '{PlaylistName}'", playlist.Name);
                    continue;
                }

                var filePath = GetUniqueBatchExportPath(directoryPath, playlist.Name, reservedFileNames);

                if (await ExportLoadedPlaylistAsync(playlist, songs, filePath).ConfigureAwait(false))
                {
                    exportedCount++;
                    totalSongs += songs.Count;
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

    private async Task<bool> ExportLoadedPlaylistAsync(
        Playlist playlist,
        IReadOnlyList<Song> songs,
        string filePath)
    {
        try
        {
            await File.WriteAllLinesAsync(filePath, GetPlaylistLines(playlist, songs), Encoding.UTF8).ConfigureAwait(false);

            _logger.LogInformation("Exported playlist '{PlaylistName}' with {SongCount} songs to {FilePath}",
                playlist.Name, songs.Count, filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist {PlaylistId} to {FilePath}", playlist.Id, filePath);
            return false;
        }
    }

    private static IEnumerable<string> GetPlaylistLines(Playlist playlist, IEnumerable<Song> songs)
    {
        yield return "#EXTM3U";
        yield return $"#PLAYLIST:{playlist.Name.ReplaceLineEndings(" ")}";
        foreach (var song in songs)
        {
            yield return $"#EXTINF:{(int)song.Duration.TotalSeconds},{song.ArtistName.ReplaceLineEndings(" ")} - {song.Title.ReplaceLineEndings(" ")}";
            yield return song.FilePath;
        }
    }

    private static string GetUniqueBatchExportPath(
        string directoryPath,
        string playlistName,
        HashSet<string> reservedFileNames)
    {
        var safeName = FileNameHelper.SanitizeFileName(playlistName, "playlist");
        const int maxBaseNameLength = 200;
        if (safeName.Length > maxBaseNameLength)
        {
            safeName = safeName[..maxBaseNameLength].TrimEnd();
        }

        var fileName = $"{safeName}.m3u8";
        for (var suffix = 2; !reservedFileNames.Add(fileName); suffix++)
        {
            fileName = $"{safeName} ({suffix}).m3u8";
        }

        return Path.Combine(directoryPath, fileName);
    }

    /// <inheritdoc />
    public async Task<BatchImportResult> ImportMultiplePlaylistsAsync(IEnumerable<string> filePaths)
    {
        var importedCount = 0;
        var totalMatched = 0;
        var totalUnmatched = 0;
        var failedFiles = new List<string>();

        foreach (var filePath in filePaths)
        {
            var playlistName = Path.GetFileNameWithoutExtension(filePath);
            var result = await ImportPlaylistAsync(filePath, playlistName).ConfigureAwait(false);

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
}
