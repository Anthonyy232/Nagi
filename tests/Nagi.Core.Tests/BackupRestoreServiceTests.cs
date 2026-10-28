using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class BackupRestoreServiceTests : IDisposable
{
    private readonly IPathConfiguration _pathConfig;
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly BackupRestoreService _service;

    private readonly List<string> _tempDirs = [];

    public BackupRestoreServiceTests()
    {
        _pathConfig = Substitute.For<IPathConfiguration>();
        var logger = Substitute.For<ILogger<BackupRestoreService>>();
        _service = new BackupRestoreService(_pathConfig, _settings, logger);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task ValidateBackupAsync_WhenFileDoesNotExist_ReturnsFalse()
    {
        var result = await _service.ValidateBackupAsync(@"C:\nonexistent\backup.zip");

        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("nagi.db", true)]
    [InlineData("settings.json", true)]
    [InlineData("some_other_file.txt", false)]
    public async Task ValidateBackupAsync_RequiresDatabaseOrSettings(string entryName, bool expected)
    {
        var zipPath = CreateTempZip((entryName, "content"));

        var result = await _service.ValidateBackupAsync(zipPath);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task ValidateBackupAsync_WithCorruptOrNonZipFile_ReturnsFalse()
    {
        var path = Path.Combine(CreateTempDir(), "corrupt.zip");
        await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x02, 0x03]);

        var result = await _service.ValidateBackupAsync(path);

        result.Should().BeFalse("a corrupt or non-ZIP file must not pass validation");
    }

    [Fact]
    public async Task CreateBackupAsync_WhenSourceDirectoryDoesNotExist_ReturnsFailure()
    {
        _pathConfig.AppDataRoot.Returns(@"C:\nonexistent\appdata");
        var destDir = CreateTempDir();

        var result = await _service.CreateBackupAsync(destDir);

        result.Success.Should().BeFalse();
        result.BackupFilePath.Should().BeNull();
    }

    [Fact]
    public async Task CreateBackupAsync_CreatesZipContainingSourceFiles()
    {
        var sourceDir = CreateTempDir();
        var destDir = CreateTempDir();
        using (OpenTestDatabase(Path.Combine(sourceDir, "nagi.db"), "Track")) { }
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "playback_state.json"), "{ }");
        _pathConfig.AppDataRoot.Returns(sourceDir);

        var result = await _service.CreateBackupAsync(destDir);

        result.Success.Should().BeTrue();
        result.BackupFilePath.Should().NotBeNull();
        File.Exists(result.BackupFilePath!).Should().BeTrue("the ZIP file should be created at the reported path");

        using var archive = ZipFile.OpenRead(result.BackupFilePath!);
        archive.Entries.Should().Contain(e => e.FullName == "nagi.db");
        archive.Entries.Should().Contain(e => e.FullName == "playback_state.json");
    }

    [Theory]
    [InlineData("Library")]
    [InlineData("Library;Archive")]
    public async Task CreateBackupAsync_WithActiveReader_IncludesCommittedWalWrites(string directoryName)
    {
        var sourceDir = Directory.CreateDirectory(Path.Combine(CreateTempDir(), directoryName)).FullName;
        var destinationDir = CreateTempDir();
        var extractedDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(sourceDir);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(sourceDir, "nagi.db"),
            Pooling = false
        }.ToString();
        using var writer = new SqliteConnection(connectionString);
        writer.Open();
        using var write = writer.CreateCommand();
        write.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE Songs (Title TEXT); INSERT INTO Songs VALUES ('Original'); PRAGMA wal_checkpoint(TRUNCATE);";
        write.ExecuteNonQuery();

        using var reader = new SqliteConnection(connectionString);
        reader.Open();
        using var transaction = reader.BeginTransaction(deferred: true);
        using var read = reader.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT COUNT(*) FROM Songs;";
        read.ExecuteScalar().Should().Be(1L);
        write.CommandText = "INSERT INTO Songs VALUES ('Recent');";
        write.ExecuteNonQuery();

        var result = await _service.CreateBackupAsync(destinationDir);

        result.Success.Should().BeTrue(result.ErrorMessage);
        ZipFile.ExtractToDirectory(result.BackupFilePath!, extractedDir);
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(extractedDir, "nagi.db"),
            Pooling = false
        }.ToString());
        backup.Open();
        using var verify = backup.CreateCommand();
        verify.CommandText = "PRAGMA integrity_check;";
        verify.ExecuteScalar().Should().Be("ok");
        verify.CommandText = "SELECT Title FROM Songs ORDER BY rowid DESC LIMIT 1;";
        verify.ExecuteScalar().Should().Be("Recent", "a backup must include committed writes even when a reader blocks checkpointing");
        read.ExecuteScalar().Should().Be(1L, "the existing reader should retain its original snapshot");
    }

    [Fact]
    public async Task CreateBackupAsync_WhenDatabaseSnapshotFails_DoesNotPublishArchive()
    {
        var sourceDir = CreateTempDir();
        var destinationDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "nagi.db"), "SQLite format 3\0corrupt data");

        var result = await _service.CreateBackupAsync(destinationDir);

        result.Success.Should().BeFalse();
        result.BackupFilePath.Should().BeNull();
        Directory.GetFiles(destinationDir).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateBackupAsync_RepeatedBackups_PreserveEveryArchive()
    {
        var sourceDir = CreateTempDir();
        var destinationDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(sourceDir);
        var paths = new List<string>();

        for (var i = 0; i < 4; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "playback_state.json"), $"{{\"version\":{i}}}");
            var result = await _service.CreateBackupAsync(destinationDir);
            result.Success.Should().BeTrue(result.ErrorMessage);
            paths.Add(result.BackupFilePath!);
        }

        paths.Should().OnlyHaveUniqueItems();
        for (var i = 0; i < paths.Count; i++)
        {
            using var archive = ZipFile.OpenRead(paths[i]);
            using var reader = new StreamReader(archive.GetEntry("playback_state.json")!.Open());
            (await reader.ReadToEndAsync()).Should().Be($"{{\"version\":{i}}}");
        }
        Directory.GetFiles(destinationDir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task CreateBackupAsync_ExcludesTemporaryAndLockFiles()
    {
        var sourceDir = CreateTempDir();
        var destDir = CreateTempDir();
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "playback_state.json"), "{ }");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, ".libvlc-cache-version"), "1");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "temp.tmp"), "tmp");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "data.lock"), "lock");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "nagi.db-wal"), "wal");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "nagi.db-shm"), "shm");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "nagi.db-journal"), "journal");
        _pathConfig.AppDataRoot.Returns(sourceDir);

        var result = await _service.CreateBackupAsync(destDir);

        result.Success.Should().BeTrue();
        using var archive = ZipFile.OpenRead(result.BackupFilePath!);
        archive.Entries.Should().NotContain(e => e.FullName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        archive.Entries.Should().NotContain(e => e.FullName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
        archive.Entries.Should().NotContain(e => e.FullName.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase));
        archive.Entries.Should().NotContain(e => e.FullName.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase));
        archive.Entries.Should().NotContain(e => e.FullName.EndsWith(".db-journal", StringComparison.OrdinalIgnoreCase));
        archive.Entries.Should().NotContain(e => e.FullName.Equals(".libvlc-cache-version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateBackupAsync_ExcludesTransientDirectories()
    {
        var sourceDir = CreateTempDir();
        var destDir = CreateTempDir();
        foreach (var dirName in new[] { "Logs", "Cache", "Temp", "EBWebView", "crashdumps" })
        {
            var dir = Directory.CreateDirectory(Path.Combine(sourceDir, dirName));
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "file.txt"), "content");
        }
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "playback_state.json"), "{ }");
        _pathConfig.AppDataRoot.Returns(sourceDir);

        var result = await _service.CreateBackupAsync(destDir);

        result.Success.Should().BeTrue();
        using var archive = ZipFile.OpenRead(result.BackupFilePath!);
        foreach (var excluded in new[] { "Logs/", "Cache/", "Temp/", "EBWebView/", "crashdumps/" })
            archive.Entries.Should().NotContain(e => e.FullName.StartsWith(excluded, StringComparison.OrdinalIgnoreCase),
                $"{excluded} should be excluded from backups");
    }

    [Fact]
    public async Task CreateBackupAsync_IncludesNonExcludedSubdirectories()
    {
        var sourceDir = CreateTempDir();
        var destDir = CreateTempDir();
        var albumArtDir = Directory.CreateDirectory(Path.Combine(sourceDir, "AlbumArt"));
        await File.WriteAllTextAsync(Path.Combine(albumArtDir.FullName, "cover.jpg"), "fake image");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "playback_state.json"), "{ }");
        _pathConfig.AppDataRoot.Returns(sourceDir);

        var result = await _service.CreateBackupAsync(destDir);

        result.Success.Should().BeTrue();
        using var archive = ZipFile.OpenRead(result.BackupFilePath!);
        archive.Entries.Should().Contain(e => e.FullName.StartsWith("AlbumArt/", StringComparison.OrdinalIgnoreCase),
            "non-excluded subdirectories should be included in the backup");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithSubdirectoryInBackup_RestoresDirectory()
    {
        var zipPath = CreateDatabaseBackup("Restored track", ("AlbumArt/cover.jpg", "content"));

        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(destDir, "AlbumArt")).Should().BeTrue("subdirectory should be restored");
        File.Exists(Path.Combine(destDir, "AlbumArt", "cover.jpg")).Should().BeTrue();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithInvalidBackupFile_ReturnsFailure()
    {
        var zipPath = CreateTempZip(("unrelated.txt", "data"));
        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeFalse();
        result.RestoredFileCount.Should().Be(0);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithValidBackup_RestoresFilesToAppDataRoot()
    {
        var zipPath = CreateDatabaseBackup("Restored track", ("playback_state.json", "restored state"));
        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(destDir, "nagi.db")).Should().BeTrue("nagi.db should be restored to AppDataRoot");
        File.Exists(Path.Combine(destDir, "playback_state.json")).Should().BeTrue("playback_state.json should be restored to AppDataRoot");
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(destDir, "nagi.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        database.Open();
        using var query = database.CreateCommand();
        query.CommandText = "SELECT Title FROM Songs;";
        query.ExecuteScalar().Should().Be("Restored track");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithOldWal_RestoresBackupInsteadOfReplayingOldLibrary()
    {
        var backupDir = CreateTempDir();
        var destDir = CreateTempDir();
        var backupDb = Path.Combine(backupDir, "nagi.db");
        var liveDb = Path.Combine(destDir, "nagi.db");
        byte[] oldWal;
        using (var connection = OpenTestDatabase(backupDb, "Backup library")) { }
        using (var connection = OpenTestDatabase(liveDb, "Current library"))
        {
            using var wal = new FileStream(liveDb + "-wal", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var bytes = new MemoryStream();
            await wal.CopyToAsync(bytes);
            oldWal = bytes.ToArray();
        }
        // Recreate journal files left behind by an interrupted process, without a live connection.
        await File.WriteAllBytesAsync(liveDb + "-wal", oldWal);
        await File.WriteAllTextAsync(liveDb + "-journal", "old rollback journal");
        var zipPath = Path.Combine(CreateTempDir(), "backup.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(backupDb, "nagi.db");
        _pathConfig.AppDataRoot.Returns(destDir);

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeTrue(result.ErrorMessage);
        using var restored = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = liveDb,
            Pooling = false
        }.ToString());
        restored.Open();
        using var verify = restored.CreateCommand();
        verify.CommandText = "SELECT Title FROM Songs;";
        verify.ExecuteScalar().Should().Be("Backup library", "journals from the replaced database must never replay into the backup");
        verify.CommandText = "PRAGMA integrity_check;";
        verify.ExecuteScalar().Should().Be("ok");
        File.Exists(liveDb + "-journal").Should().BeFalse();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WhenRestoreFails_RollsBackDatabaseAndItsJournals()
    {
        var zipPath = CreateDatabaseBackup("Backup track", ("playback_state.json", "backup state"));
        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);
        var originalFiles = new[] { "nagi.db", "nagi.db-wal", "nagi.db-shm", "nagi.db-journal", "playback_state.json" };
        foreach (var name in originalFiles)
            await File.WriteAllTextAsync(Path.Combine(destDir, name), $"original {name}");

        RestoreResult result;
        using (File.Open(Path.Combine(destDir, "playback_state.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeFalse();
        foreach (var name in originalFiles)
            (await File.ReadAllTextAsync(Path.Combine(destDir, name))).Should().Be($"original {name}");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithActiveDatabaseConnection_PreservesLiveLibrary()
    {
        var zipPath = CreateDatabaseBackup("Backup track");
        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);
        using var connection = OpenTestDatabase(Path.Combine(destDir, "nagi.db"), "Live library");

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeFalse();
        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT Title FROM Songs;";
        verify.ExecuteScalar().Should().Be("Live library");
        verify.CommandText = "INSERT INTO Songs VALUES ('Still writable');";
        verify.ExecuteNonQuery().Should().Be(1);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithSettingsOnly_PreservesDatabaseJournals()
    {
        var zipPath = CreateTempZip(("settings.json", "backup settings"));
        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);
        var databaseFiles = new[] { "nagi.db", "nagi.db-wal", "nagi.db-shm", "nagi.db-journal" };
        foreach (var name in databaseFiles)
            await File.WriteAllTextAsync(Path.Combine(destDir, name), $"original {name}");

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeTrue(result.ErrorMessage);
        foreach (var name in databaseFiles)
            (await File.ReadAllTextAsync(Path.Combine(destDir, name))).Should().Be($"original {name}");
    }

    [Theory]
    [InlineData("nagi.db-wal")]
    [InlineData("nagi.db-shm")]
    [InlineData("nagi.db-journal")]
    public async Task RestoreFromBackupAsync_WithLockedJournal_PreservesLiveDatabase(string lockedFile)
    {
        var zipPath = CreateDatabaseBackup("Backup track");
        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);
        var databaseFiles = new[] { "nagi.db", "nagi.db-wal", "nagi.db-shm", "nagi.db-journal" };
        foreach (var name in databaseFiles)
            await File.WriteAllTextAsync(Path.Combine(destDir, name), $"original {name}");

        RestoreResult result;
        using (File.Open(Path.Combine(destDir, lockedFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeFalse();
        foreach (var name in databaseFiles)
            (await File.ReadAllTextAsync(Path.Combine(destDir, name))).Should().Be($"original {name}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreFromBackupAsync_DamagedDatabase_DoesNotReplaceAnyLiveFiles(bool truncateDatabase)
    {
        var archivePath = truncateDatabase
            ? CreateDatabaseBackup("Backup track", ("playback_state.json", "new state"))
            : CreateTempZip(("nagi.db", "not a database"), ("playback_state.json", "new state"));
        if (truncateDatabase)
        {
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
            var entry = archive.GetEntry("nagi.db")!;
            using var stream = entry.Open();
            stream.SetLength(512);
        }
        var destination = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destination);
        var databasePath = Path.Combine(destination, "nagi.db");
        using (OpenTestDatabase(databasePath, "Live track")) { }
        var originalDatabase = await File.ReadAllBytesAsync(databasePath);
        var statePath = Path.Combine(destination, "playback_state.json");
        await File.WriteAllTextAsync(statePath, "live state");

        var result = await _service.RestoreFromBackupAsync(archivePath);

        result.Success.Should().BeFalse();
        (await File.ReadAllBytesAsync(databasePath)).Should().Equal(originalDatabase);
        (await File.ReadAllTextAsync(statePath)).Should().Be("live state");
    }

    [Fact]
    public async Task CreateBackupAsync_StoresTheSettingsSnapshotOverAnyLegacySettingsFile()
    {
        var sourceDir = CreateTempDir();
        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "settings.json"), "legacy settings");
        _settings.ExportAsync().Returns("snapshot");

        var result = await _service.CreateBackupAsync(destDir);

        using var archive = ZipFile.OpenRead(result.BackupFilePath!);
        using var reader = new StreamReader(archive.GetEntry("settings.json")!.Open());
        (await reader.ReadToEndAsync()).Should().Be("snapshot");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ImportsSettingsInsteadOfCopyingTheSnapshot()
    {
        var zipPath = CreateDatabaseBackup("Backup track", ("settings.json", "snapshot"));
        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeTrue(result.ErrorMessage);
        await _settings.Received(1).ImportAsync("snapshot");
        File.Exists(Path.Combine(destDir, "settings.json")).Should().BeFalse();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WithSettingsInAnOldFormat_StillRestoresTheLibrary()
    {
        var zipPath = CreateDatabaseBackup("Backup track", ("settings.json", "{\"Volume\":0.5}"));
        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);
        _settings.ImportAsync(Arg.Any<string>()).Returns(Task.FromException(new JsonException("old format")));

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeTrue(result.ErrorMessage);
        File.Exists(Path.Combine(destDir, "nagi.db")).Should().BeTrue();
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WhenFilesFailToRestore_LeavesSettingsUntouched()
    {
        var zipPath = CreateTempZip(("nagi.db", "not a database"), ("settings.json", "snapshot"));
        _pathConfig.AppDataRoot.Returns(CreateTempDir());

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeFalse();
        await _settings.DidNotReceiveWithAnyArgs().ImportAsync(default!);
    }

    private string CreateTempDir()
    {
        var path = Directory.CreateTempSubdirectory("Nagi.BackupTests_").FullName;
        _tempDirs.Add(path);
        return path;
    }

    private string CreateTempZip(params (string name, string content)[] entries)
    {
        var path = Path.Combine(CreateTempDir(), "backup.zip");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }

    private string CreateDatabaseBackup(string title, params (string name, string content)[] files)
    {
        var databasePath = Path.Combine(CreateTempDir(), "nagi.db");
        using (OpenTestDatabase(databasePath, title)) { }
        var archivePath = CreateTempZip(files);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        archive.CreateEntryFromFile(databasePath, "nagi.db");
        return archivePath;
    }

    private static SqliteConnection OpenTestDatabase(string path, string title)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE Songs (Title TEXT); INSERT INTO Songs VALUES ($title);";
        command.Parameters.AddWithValue("$title", title);
        command.ExecuteNonQuery();
        return connection;
    }
}
