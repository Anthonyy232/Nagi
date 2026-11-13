namespace Nagi.Core.Backup;

public interface IBackupRestoreService
{
    /// <summary>
    ///     Saves the library, its caches and the settings to a new archive in the folder. A scan the user started and
    ///     the server syncs are cancelled so the library doesn't change while it is copied.
    /// </summary>
    Task<BackupResult> CreateBackupAsync(string destinationFolderPath);

    /// <summary>
    ///     Replaces the library, its caches and the settings with the backup's. A scan the user started and the server
    ///     syncs are cancelled, then the queue is cleared, and the app should restart afterwards.
    /// </summary>
    Task<RestoreResult> RestoreFromBackupAsync(string backupFilePath);

    Task<bool> ValidateBackupAsync(string backupFilePath);
}
