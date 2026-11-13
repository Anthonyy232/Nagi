namespace Nagi.Core.Backup;

public record BackupResult(bool Success, string? BackupFilePath, double BackupSizeMB);

/// <param name="ErrorMessage">A localized reason for the user, or null when the generic failure message fits.</param>
public record RestoreResult(bool Success, string? ErrorMessage = null);
