namespace Nagi.Core.Services.Data;

/// <summary>
///     Represents the result of a playlist export operation.
/// </summary>
/// <param name="Success">Whether the export succeeded.</param>
/// <param name="SongCount">The number of songs exported.</param>
/// <param name="ErrorMessage">An error message if the export failed.</param>
public record PlaylistExportResult(bool Success, int SongCount, string? ErrorMessage = null);

/// <summary>
///     Represents the result of a playlist import operation.
/// </summary>
/// <param name="Success">Whether the import succeeded.</param>
/// <param name="PlaylistId">The ID of the newly created playlist, if successful.</param>
/// <param name="MatchedSongs">The number of songs successfully matched to the library.</param>
/// <param name="UnmatchedSongs">The number of songs that could not be matched.</param>
/// <param name="UnmatchedPaths">The file paths that could not be matched.</param>
/// <param name="ErrorMessage">An error message if the import failed.</param>
public record PlaylistImportResult(
    bool Success,
    Guid? PlaylistId,
    int MatchedSongs,
    int UnmatchedSongs,
    List<string> UnmatchedPaths,
    string? ErrorMessage = null);

/// <summary>
///     Represents the result of a batch playlist export operation.
/// </summary>
/// <param name="Success">Whether the export succeeded.</param>
/// <param name="PlaylistsExported">The number of playlists successfully exported.</param>
/// <param name="TotalSongs">The total number of songs exported across all playlists.</param>
/// <param name="ErrorMessage">An error message if the export failed.</param>
public record BatchExportResult(bool Success, int PlaylistsExported, int TotalSongs, string? ErrorMessage = null);

/// <summary>
///     Represents the result of a batch playlist import operation.
/// </summary>
/// <param name="Success">Whether any imports succeeded.</param>
/// <param name="PlaylistsImported">The number of playlists successfully imported.</param>
/// <param name="TotalMatchedSongs">The total number of songs matched across all playlists.</param>
/// <param name="TotalUnmatchedSongs">The total number of songs that could not be matched.</param>
/// <param name="FailedFiles">Files that failed to import.</param>
public record BatchImportResult(
    bool Success,
    int PlaylistsImported,
    int TotalMatchedSongs,
    int TotalUnmatchedSongs,
    List<string> FailedFiles);
