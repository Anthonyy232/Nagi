namespace Nagi.Core.Backup;

public interface IBackupRestoreService
{
    Task<BackupResult> CreateBackupAsync(string destinationFolderPath);

    /// <summary>
    ///     Replaces the library, its caches and the settings with the backup's. The queue is cleared first, and the app
    ///     should restart afterwards.
    /// </summary>
    Task<RestoreResult> RestoreFromBackupAsync(string backupFilePath);

    Task<bool> ValidateBackupAsync(string backupFilePath);
}
