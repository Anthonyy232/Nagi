namespace Nagi.Core.Services.Data;

public record PlaylistImportResult(
    bool Success,
    Guid? PlaylistId,
    int MatchedSongs,
    int UnmatchedSongs,
    List<string> UnmatchedPaths,
    string? ErrorMessage = null);

public record BatchExportResult(bool Success, int PlaylistsExported, int TotalSongs, string? ErrorMessage = null);

public record BatchImportResult(
    bool Success,
    int PlaylistsImported,
    int TotalMatchedSongs,
    int TotalUnmatchedSongs,
    List<string> FailedFiles);
