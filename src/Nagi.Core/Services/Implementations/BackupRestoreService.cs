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
                var directoryInfo = new DirectoryInfo(sourcePath);
                
                // Exclusions
                var excludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { 
                    "Logs", 
                    "EBWebView", 
                    "Temp", 
                    "Cache",
                    "packages",      // Velopack/Squirrel
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

                    // Skip updater files
                    if (file.Name.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) ||
                        file.Extension.Equals(".nupkg", StringComparison.OrdinalIgnoreCase) ||
                        file.Name.StartsWith("Squirrel", StringComparison.OrdinalIgnoreCase))
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
                    
                    // Exclude versioned app folders (e.g., app-1.0.0)
                    if (dir.Name.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) continue;

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
        try
        {
            if (!await ValidateBackupAsync(backupFilePath))
            {
                return new RestoreResult(false, 0, false, "Invalid backup file.");
            }

            var destPath = _pathConfiguration.AppDataRoot;
            Directory.CreateDirectory(destPath); // Ensure it exists

            // Extract to temp first to verify integrity and structure
            var stagingPath = Path.Combine(Path.GetTempPath(), $"NagiRestoreStaging_{Guid.NewGuid()}");
            Directory.CreateDirectory(stagingPath);

            try
            {
                ZipFile.ExtractToDirectory(backupFilePath, stagingPath);
                
                // If extraction succeeded, we proceed to overwrite live data.
                // NOTE: We cannot easily "close" the db connection from here if it's open by EF Core.
                // The app restart requirement handles the final consistency, but replacing open files might fail on Windows.
                try 
                {
                    SqliteConnection.ClearAllPools();
                    // Force GC to finalize any lingering contexts/commands
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clear SQLite connection pools. Restore may fail if DB is locked.");
                }

                var restoredCount = 0;

                // Move files from staging to dest
                foreach (var file in Directory.GetFiles(stagingPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileName(file);
                    var destFile = Path.Combine(destPath, fileName);
                    
                    try 
                    {
                        await RetryFileOperationAsync(() => File.Copy(file, destFile, true));
                    }
                    catch (IOException)
                    {
                        // If file is still locked (like nagi.db), try renaming it to .old and then copying
                        // This relies on the file being opened with FileShare.Delete which is common for SQLite
                        var oldFile = destFile + ".old." + Guid.NewGuid();
                        try 
                        {
                            await RetryFileOperationAsync(() => File.Move(destFile, oldFile));
                            await RetryFileOperationAsync(() => File.Copy(file, destFile, true));
                            
                            // Try to delete the old file, ignore if it fails (it will be cleaned up later or left as garbage)
                            try { File.Delete(oldFile); } catch { }
                        }
                        catch (Exception ex)
                        {
                            // If rename also fails, we can't restore this file
                            _logger.LogError(ex, "Failed to restore locked file: {FileName}", fileName);
                            throw; // Abort restore to avoid partial state? Or continue? 
                                   // Better to abort and tell user to restart manually/close app.
                        }
                    }
                    
                    restoredCount++;
                }

                foreach (var dir in Directory.GetDirectories(stagingPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var dirName = Path.GetFileName(dir);
                    var destDir = Path.Combine(destPath, dirName);
                    
                    if (Directory.Exists(destDir))
                    {
                        await RetryFileOperationAsync(() => Directory.Delete(destDir, true));
                    }
                    
                    // MoveDirectory is atomic-ish on same volume, but CopyDirectory is safer across volumes/temp
                    // Since temp might be different volume, let's use recursive copy or move helper
                    // Here we can just move since it's staging
                    await RetryFileOperationAsync(() => Directory.Move(dir, destDir));
                    restoredCount++; // Counting directories as "items"
                }

                _logger.LogInformation("Restore completed successfully. {Count} items restored.", restoredCount);
                return new RestoreResult(true, restoredCount, true);
            }
            finally
            {
                 if (Directory.Exists(stagingPath))
                {
                    try 
                    {
                        Directory.Delete(stagingPath, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clean up staging directory {Path}", stagingPath);
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
}
