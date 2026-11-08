namespace Nagi.Core.Backup;

/// <param name="ErrorMessage">A localized reason for the user, or null when the generic failure message fits.</param>
public record BackupResult(bool Success, string? BackupFilePath, double BackupSizeMB, string? ErrorMessage = null);

/// <param name="ErrorMessage">A localized reason for the user, or null when the generic failure message fits.</param>
public record RestoreResult(bool Success, string? ErrorMessage = null);
