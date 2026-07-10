using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using System.Threading;

namespace Nagi.Core.Services.Implementations;

public class BackupRestoreService : IBackupRestoreService
{
    private readonly IPathConfiguration _pathConfiguration;
    private readonly IFileSystemService _fileSystemService;
    private readonly ILogger<BackupRestoreService> _logger;

    public BackupRestoreService(
        IPathConfiguration pathConfiguration,
        IFileSystemService fileSystemService,
        ILogger<BackupRestoreService> logger)
    {
        _pathConfiguration = pathConfiguration;
        _fileSystemService = fileSystemService;
        _logger = logger;
    }

    public async Task<BackupResult> CreateBackupAsync(string destinationFolderPath)
    {
        try
        {
            var sourcePath = _pathConfiguration.AppDataRoot;

            if (!Directory.Exists(sourcePath))
            {
                return new BackupResult(false, null, 0, "Source directory not found.");
            }

            // Create staging directory
            var stagingPath = Path.Combine(Path.GetTempPath(), $"NagiBackupStaging_{Guid.NewGuid()}");
            Directory.CreateDirectory(stagingPath);

            try
            {
                // Flush WAL to the main database file so the backup includes all recent writes.
                // Without this, any data written since the last automatic checkpoint would be lost
                // because .db-wal/.db-shm files are intentionally excluded from the backup.
                try
                {
                    var dbPath = Path.Combine(sourcePath, "nagi.db");
                    if (File.Exists(dbPath) && IsSqliteDatabase(dbPath))
                    {
                        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
                        {
                            connection.Open();
                            using var cmd = connection.CreateCommand();
                            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                            cmd.ExecuteNonQuery();
                        }
                        // Release all pooled handles so the file is not locked during backup copy
                        SqliteConnection.ClearAllPools();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to checkpoint WAL before backup. Backup may be missing recent data.");
                }

                var directoryInfo = new DirectoryInfo(sourcePath);

                // Exclusions
                var excludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Logs",
                    "EBWebView",
                    "Temp",
                    "Cache",
                    "crashdumps"

                };

                // Copy all files in root
                foreach (var file in directoryInfo.GetFiles())
                {
                    // Skip temporary/lock files
                    if (file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        file.Extension.Equals(".lock", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }



                    // Skip cache files
                    if (file.Name.Equals(".libvlc-cache-version", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Skip SQLite temporary files (safer for live backups)
                    if (file.Extension.Equals(".db-wal", StringComparison.OrdinalIgnoreCase) ||
                        file.Extension.Equals(".db-shm", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    await CopyFileAsync(file.FullName, Path.Combine(stagingPath, file.Name));
                }

                // Copy directories recursively (except exclusions)
                foreach (var dir in directoryInfo.GetDirectories())
                {
                    if (excludedDirectories.Contains(dir.Name)) continue;



                    var destDir = Path.Combine(stagingPath, dir.Name);
                    await CopyDirectoryAsync(dir.FullName, destDir);
                }

                // Create Zip
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var zipFileName = $"Nagi_Backup_{timestamp}.zip";
                var zipFilePath = Path.Combine(destinationFolderPath, zipFileName);

                if (File.Exists(zipFilePath))
                {
                    File.Delete(zipFilePath);
                }

                ZipFile.CreateFromDirectory(stagingPath, zipFilePath);

                var fileInfo = new FileInfo(zipFilePath);
                var sizeMb = fileInfo.Length / (1024.0 * 1024.0);

                _logger.LogInformation("Backup created successfully at {Path}. Size: {Size} MB", zipFilePath, sizeMb);

                return new BackupResult(true, zipFilePath, sizeMb);
            }
            finally
            {
                // Cleanup staging
                if (Directory.Exists(stagingPath))
                {
                    Directory.Delete(stagingPath, true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup.");
            return new BackupResult(false, null, 0, ex.Message);
        }
    }

    public async Task<RestoreResult> RestoreFromBackupAsync(string backupFilePath)
    {
        string? transactionPath = null;
        var preserveTransaction = false;

        try
        {
            if (!await ValidateBackupAsync(backupFilePath))
            {
                return new RestoreResult(false, 0, false, "Invalid backup file.");
            }

            var destinationDirectory = new DirectoryInfo(Path.GetFullPath(_pathConfiguration.AppDataRoot));
            var destPath = destinationDirectory.FullName;
            Directory.CreateDirectory(destPath); // Ensure it exists

            // Keep staging and rollback data beside AppDataRoot so every move stays on the
            // same volume. This lets us reverse the complete restore if any item fails.
            var destinationParent = destinationDirectory.Parent?.FullName
                ?? throw new InvalidOperationException("The application data directory must have a parent directory.");
            transactionPath = Path.Combine(
                destinationParent,
                $".{destinationDirectory.Name}.restore-{Guid.NewGuid():N}");
            var incomingPath = Path.Combine(transactionPath, "incoming");
            var rollbackPath = Path.Combine(transactionPath, "rollback");
            Directory.CreateDirectory(incomingPath);
            Directory.CreateDirectory(rollbackPath);

            try
            {
                ZipFile.ExtractToDirectory(backupFilePath, incomingPath);

                // If extraction succeeded, we proceed to overwrite live data.
                // NOTE: We cannot easily "close" the db connection from here if it's open by EF Core.
                // The app restart requirement handles the final consistency, but replacing open files might fail on Windows.
                try
                {
                    SqliteConnection.ClearAllPools();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clear SQLite connection pools. Restore may fail if DB is locked.");
                }

                var journal = new List<RestoreJournalEntry>();
                var restoredCount = 0;

                try
                {
                    // A stable order makes failures and rollback behavior deterministic.
                    foreach (var incomingItem in Directory
                        .EnumerateFileSystemEntries(incomingPath, "*", SearchOption.TopDirectoryOnly)
                        .OrderBy(item => Path.GetFileName(item), StringComparer.OrdinalIgnoreCase))
                    {
                        var itemName = Path.GetFileName(incomingItem);
                        var destinationItem = Path.Combine(destPath, itemName);
                        string? originalItem = null;

                        if (EntryExists(destinationItem))
                        {
                            originalItem = Path.Combine(rollbackPath, itemName);
                            await RetryFileOperationAsync(() => MoveEntry(destinationItem, originalItem));
                        }

                        // Record the original before installing the replacement so this item
                        // participates in rollback even if the second move fails.
                        journal.Add(new RestoreJournalEntry(destinationItem, originalItem));
                        await RetryFileOperationAsync(() => MoveEntry(incomingItem, destinationItem));
                        restoredCount++;
                    }
                }
                catch (Exception restoreException)
                {
                    var rollbackErrors = await RollbackRestoreAsync(journal);
                    if (rollbackErrors.Count > 0)
                    {
                        preserveTransaction = true;
                        var rollbackMessage = $"Restore failed and rollback was incomplete. Recovery data was preserved at '{transactionPath}'.";
                        _logger.LogCritical(
                            restoreException,
                            "{Message} Rollback errors: {RollbackErrors}",
                            rollbackMessage,
                            string.Join(" | ", rollbackErrors.Select(error => error.Message)));
                        return new RestoreResult(false, 0, false, rollbackMessage);
                    }

                    _logger.LogError(restoreException, "Restore failed; all replaced items were rolled back.");
                    return new RestoreResult(false, 0, false, restoreException.Message);
                }

                _logger.LogInformation("Restore completed successfully. {Count} items restored.", restoredCount);
                return new RestoreResult(true, restoredCount, true);
            }
            finally
            {
                if (!preserveTransaction && Directory.Exists(transactionPath))
                {
                    try
                    {
                        Directory.Delete(transactionPath, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clean up restore transaction directory {Path}", transactionPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore backup from {Path}", backupFilePath);
            return new RestoreResult(false, 0, false, ex.Message);
        }
    }

    private async Task<List<Exception>> RollbackRestoreAsync(List<RestoreJournalEntry> journal)
    {
        var errors = new List<Exception>();

        foreach (var entry in journal.AsEnumerable().Reverse())
        {
            try
            {
                if (EntryExists(entry.DestinationPath))
                {
                    await RetryFileOperationAsync(() => DeleteEntry(entry.DestinationPath));
                }

                if (entry.OriginalPath is not null && EntryExists(entry.OriginalPath))
                {
                    await RetryFileOperationAsync(() => MoveEntry(entry.OriginalPath, entry.DestinationPath));
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                _logger.LogError(ex, "Failed to roll back restored item {Path}", entry.DestinationPath);
            }
        }

        return errors;
    }

    private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void MoveEntry(string sourcePath, string destinationPath)
    {
        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            File.Move(sourcePath, destinationPath);
        }
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record RestoreJournalEntry(string DestinationPath, string? OriginalPath);

    private async Task RetryFileOperationAsync(Action action, int maxRetries = 3, int delayMs = 100)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException)
            {
                if (i == maxRetries - 1) throw;
                await Task.Delay(delayMs);
            }
        }
    }

    public Task<bool> ValidateBackupAsync(string backupFilePath)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!File.Exists(backupFilePath)) return false;

                using var archive = ZipFile.OpenRead(backupFilePath);

                // Check for critical file
                var hasDb = archive.Entries.Any(e => e.FullName.Equals("nagi.db", StringComparison.OrdinalIgnoreCase));
                var hasSettings = archive.Entries.Any(e => e.FullName.Equals("settings.json", StringComparison.OrdinalIgnoreCase));

                // We require at least the DB or settings to consider it a valid backup of THIS app
                return hasDb || hasSettings;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backup validation failed for {Path}", backupFilePath);
                return false;
            }
        });
    }

    private async Task CopyFileAsync(string source, string dest)
    {
        // Use FileShare.ReadWrite to allow copying even if file is open (e.g. WAL mode DB)
        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await sourceStream.CopyToAsync(destStream);
    }

    private async Task CopyDirectoryAsync(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists) throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            await CopyFileAsync(file.FullName, targetFilePath);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            await CopyDirectoryAsync(subDir.FullName, newDestinationDir);
        }
    }

    private static bool IsSqliteDatabase(string filePath)
    {
        try
        {
            // SQLite files start with the 16-byte magic string "SQLite format 3\000"
            Span<byte> header = stackalloc byte[16];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8);
        }
        catch
        {
            return false;
        }
    }
}
