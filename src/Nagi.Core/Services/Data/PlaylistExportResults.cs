namespace Nagi.Core.Services.Data;

public record PlaylistImportResult(bool Success, int MatchedSongs, int UnmatchedSongs);

public record BatchExportResult(bool Success, int PlaylistsExported, int TotalSongs, string? ErrorMessage = null);

public record BatchImportResult(
    bool Success,
    int PlaylistsImported,
    int TotalMatchedSongs,
    int TotalUnmatchedSongs,
    int FailedFileCount);
