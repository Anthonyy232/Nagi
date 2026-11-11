using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Settings;

namespace Nagi.Core.Backup;

public class BackupRestoreService : IBackupRestoreService
{
    // Settings live in the platform's settings store, so the backup carries them as a snapshot file.
    private const string SettingsFileName = "settings.json";

    private readonly IPathConfiguration _pathConfiguration;
    private readonly ISettingsService _settingsService;
    private readonly IMusicPlaybackService _playback;
    private readonly ILibraryReset _libraryReset;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger<BackupRestoreService> _logger;

    public BackupRestoreService(
        IPathConfiguration pathConfiguration,
        ISettingsService settingsService,
        IMusicPlaybackService playback,
        ILibraryReset libraryReset,
        IDispatcherService dispatcher,
        ILogger<BackupRestoreService> logger)
    {
        _pathConfiguration = pathConfiguration;
        _settingsService = settingsService;
        _playback = playback;
        _libraryReset = libraryReset;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    // Entries sit at the archive's root under the names they have in the data folder.
    private string DatabaseFileName => Path.GetFileName(_pathConfiguration.DatabasePath);

    public async Task<BackupResult> CreateBackupAsync(string destinationFolderPath)
    {
        try
        {
            var stagingPath = Directory.CreateTempSubdirectory("NagiBackupStaging_").FullName;

            try
            {
                // A scan could otherwise delete a cover between listing the folder and copying the file.
                await _libraryReset.RunWhileQuietAsync("backup", async () =>
                {
                    var databasePath = _pathConfiguration.DatabasePath;
                    if (File.Exists(databasePath))
                    {
                        // SQLite takes a consistent snapshot, including committed WAL data.
                        // A checkpoint followed by a file copy can lose writes or race a writer.
                        var target = Path.Combine(stagingPath, DatabaseFileName);
                        await Task.Run(() => BackupDatabase(databasePath, target)).ConfigureAwait(false);
                    }

                    foreach (var folder in _pathConfiguration.LibraryCacheFolders.Append(_pathConfiguration.RomanizationPacksPath))
                        if (Directory.Exists(folder))
                            await CopyDirectoryAsync(folder, Path.Combine(stagingPath, Path.GetFileName(folder))).ConfigureAwait(false);
                }).ConfigureAwait(false);

                await File.WriteAllTextAsync(Path.Combine(stagingPath, SettingsFileName),
                    await _settingsService.ExportAsync().ConfigureAwait(false)).ConfigureAwait(false);

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var zipFilePath = Path.Combine(destinationFolderPath, $"Nagi_Backup_{timestamp}.zip");
                var pendingArchivePath = Path.Combine(destinationFolderPath, $".Nagi_Backup_{Guid.NewGuid():N}.tmp");
                try
                {
                    // Publish only a complete archive, and never overwrite an earlier backup.
                    await ZipFile.CreateFromDirectoryAsync(stagingPath, pendingArchivePath).ConfigureAwait(false);
                    for (var suffix = 2; ; suffix++)
                    {
                        try
                        {
                            File.Move(pendingArchivePath, zipFilePath);
                            break;
                        }
                        catch (IOException) when (File.Exists(zipFilePath))
                        {
                            zipFilePath = Path.Combine(destinationFolderPath, $"Nagi_Backup_{timestamp} ({suffix}).zip");
                        }
                    }
                }
                finally
                {
                    if (File.Exists(pendingArchivePath)) File.Delete(pendingArchivePath);
                }

                var fileInfo = new FileInfo(zipFilePath);
                var sizeMb = fileInfo.Length / (1024.0 * 1024.0);

                _logger.LogInformation("Backup created successfully at {Path}. Size: {Size} MB", zipFilePath, sizeMb);

                return new BackupResult(true, zipFilePath, sizeMb);
            }
            finally
            {

                if (Directory.Exists(stagingPath))
                {
                    Directory.Delete(stagingPath, true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup.");
            return new BackupResult(false, null, 0);
        }
    }

    public async Task<RestoreResult> RestoreFromBackupAsync(string backupFilePath)
    {
        try
        {
            if (!await ValidateBackupAsync(backupFilePath).ConfigureAwait(false))
            {
                return new RestoreResult(false, Resources.Strings.Backup_InvalidFile);
            }

            var destinationDirectory = new DirectoryInfo(Path.GetFullPath(_pathConfiguration.AppDataRoot));
            var destPath = destinationDirectory.FullName;
            Directory.CreateDirectory(destPath);

            // Keep staging and rollback data beside AppDataRoot so every move stays on the
            // same volume. This lets us reverse the complete restore if any item fails.
            var destinationParent = destinationDirectory.Parent?.FullName
                ?? throw new InvalidOperationException("The application data directory must have a parent directory.");
            var transactionPath = Path.Combine(
                destinationParent,
                $".{destinationDirectory.Name}.restore-{Guid.NewGuid():N}");
            var incomingPath = Path.Combine(transactionPath, "incoming");
            var rollbackPath = Path.Combine(transactionPath, "rollback");
            var preserveTransaction = false;

            try
            {
                Directory.CreateDirectory(incomingPath);
                Directory.CreateDirectory(rollbackPath);
                await ZipFile.ExtractToDirectoryAsync(backupFilePath, incomingPath).ConfigureAwait(false);

                var incomingSettings = Path.Combine(incomingPath, SettingsFileName);
                string? settingsSnapshot = null;
                if (File.Exists(incomingSettings))
                {
                    settingsSnapshot = await File.ReadAllTextAsync(incomingSettings).ConfigureAwait(false);
                    File.Delete(incomingSettings);
                }

                var journal = new List<RestoreJournalEntry>();
                var restoredCount = 0;
                var incomingDatabase = Path.Combine(incomingPath, DatabaseFileName);
                var existingDatabase = Path.Combine(destPath, DatabaseFileName);
                if (File.Exists(incomingDatabase))
                {
                    using var database = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = incomingDatabase,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false
                    }.ToString());
                    database.Open();
                    using var check = database.CreateCommand();
                    check.CommandText = "PRAGMA quick_check;";
                    if (check.ExecuteScalar() is not "ok")
                        throw new InvalidDataException("The backup database failed SQLite's integrity check.");
                }

                // A scan holds database connections that would block the swap, and would write the old library into
                // the restored one after it.
                RestoreResult? failure = null;
                await _libraryReset.RunWhileQuietAsync("restoring a backup", async () =>
                {
                    // The app restarts through its normal shutdown after a restore, which ends the current listen by its
                    // row ID and saves the queue. Ending them now keeps the listen in the database it belongs to and leaves
                    // nothing to write over the restored data. The playback service is changed from the UI context only.
                    await _dispatcher.EnqueueAsync(_playback.ClearQueueAsync).ConfigureAwait(false);

                    try
                    {
                        SqliteConnection.ClearAllPools();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clear SQLite connection pools. Restore may fail if DB is locked.");
                    }

                    // On Windows, deny database reads/writes during replacement and rollback,
                    // while allowing our own renames. An active SQLite connection makes this
                    // fail before any live files are changed.
                    using var incomingDatabaseLock = File.Exists(incomingDatabase)
                        ? new FileStream(incomingDatabase, FileMode.Open, FileAccess.Read, FileShare.Delete)
                        : null;
                    using var existingDatabaseLock = incomingDatabaseLock is not null && File.Exists(existingDatabase)
                        ? new FileStream(existingDatabase, FileMode.Open, FileAccess.Read, FileShare.Delete)
                        : null;

                    try
                    {
                        // A restored snapshot must not inherit journals from the replaced database.
                        // Keep them in the rollback transaction so any later failure restores the
                        // original database and its complete recovery state together.
                        if (incomingDatabaseLock is not null)
                        {
                            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                            {
                                var journalName = DatabaseFileName + suffix;
                                var existingJournal = Path.Combine(destPath, journalName);
                                if (!EntryExists(existingJournal)) continue;

                                var savedJournal = Path.Combine(rollbackPath, journalName);
                                MoveEntry(existingJournal, savedJournal);
                                journal.Add(new RestoreJournalEntry(existingJournal, savedJournal));
                            }
                        }

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
                                MoveEntry(destinationItem, originalItem);
                            }

                            // Record the original before installing the replacement so this item
                            // participates in rollback even if the second move fails.
                            journal.Add(new RestoreJournalEntry(destinationItem, originalItem));
                            MoveEntry(incomingItem, destinationItem);
                            restoredCount++;
                        }
                    }
                    catch (Exception restoreException)
                    {
                        var rollbackErrors = RollbackRestore(journal);
                        if (rollbackErrors.Count > 0)
                        {
                            preserveTransaction = true;
                            var rollbackMessage = string.Format(Resources.Strings.Backup_IncompleteRestore, transactionPath);
                            _logger.LogCritical(
                                restoreException,
                                "{Message} Rollback errors: {RollbackErrors}",
                                rollbackMessage,
                                string.Join(" | ", rollbackErrors.Select(error => error.Message)));
                            failure = new RestoreResult(false, rollbackMessage);
                            return;
                        }

                        _logger.LogError(restoreException, "Restore failed; all replaced items were rolled back.");
                        failure = new RestoreResult(false);
                    }
                }).ConfigureAwait(false);
                if (failure is not null) return failure;

                if (settingsSnapshot is not null)
                {
                    try
                    {
                        await _settingsService.ImportAsync(settingsSnapshot).ConfigureAwait(false);
                        restoredCount++;
                    }
                    catch (JsonException ex)
                    {
                        // Backups made before settings moved to the platform store keep them in another format.
                        _logger.LogWarning(ex, "Skipped settings in an unsupported format; the library was restored.");
                    }
                }

                _logger.LogInformation("Restore completed successfully. {Count} items restored.", restoredCount);
                return new RestoreResult(true);
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
            return new RestoreResult(false);
        }
    }

    private List<Exception> RollbackRestore(List<RestoreJournalEntry> journal)
    {
        var errors = new List<Exception>();

        foreach (var entry in journal.AsEnumerable().Reverse())
        {
            try
            {
                if (EntryExists(entry.DestinationPath))
                {
                    DeleteEntry(entry.DestinationPath);
                }

                if (entry.OriginalPath is not null && EntryExists(entry.OriginalPath))
                {
                    MoveEntry(entry.OriginalPath, entry.DestinationPath);
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

    public Task<bool> ValidateBackupAsync(string backupFilePath)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!File.Exists(backupFilePath)) return false;

                using var archive = ZipFile.OpenRead(backupFilePath);

                var hasDb = archive.Entries.Any(e => e.FullName.Equals(DatabaseFileName, StringComparison.OrdinalIgnoreCase));
                var hasSettings = archive.Entries.Any(e => e.FullName.Equals(SettingsFileName, StringComparison.OrdinalIgnoreCase));

                return hasDb || hasSettings;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backup validation failed for {Path}", backupFilePath);
                return false;
            }
        });
    }

    private static async Task CopyFileAsync(string source, string dest)
    {
        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await sourceStream.CopyToAsync(destStream).ConfigureAwait(false);
    }

    private static async Task CopyDirectoryAsync(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.EnumerateFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            await CopyFileAsync(file.FullName, targetFilePath).ConfigureAwait(false);
        }

        foreach (var subDir in dir.EnumerateDirectories())
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            await CopyDirectoryAsync(subDir.FullName, newDestinationDir).ConfigureAwait(false);
        }
    }

    private static void BackupDatabase(string sourcePath, string destinationPath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Pooling = false
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

}
