namespace Nagi.Core.Backup;

public interface IBackupRestoreService
{
    Task<BackupResult> CreateBackupAsync(string destinationFolderPath);
    Task<RestoreResult> RestoreFromBackupAsync(string backupFilePath);
    Task<bool> ValidateBackupAsync(string backupFilePath);
}
