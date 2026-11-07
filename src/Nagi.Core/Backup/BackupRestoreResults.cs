namespace Nagi.Core.Backup;

public record BackupResult(bool Success, string? BackupFilePath, double BackupSizeMB, string? ErrorMessage = null);

public record RestoreResult(bool Success, int RestoredFileCount, bool RequiresRestart, string? ErrorMessage = null);
