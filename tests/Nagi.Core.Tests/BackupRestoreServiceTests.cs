using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
    private readonly BackupRestoreService _service;

    // Temp dirs and files created during tests; all cleaned up in Dispose.
    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempFiles = [];

    public BackupRestoreServiceTests()
    {
        _pathConfig = Substitute.For<IPathConfiguration>();
        var fileSystem = Substitute.For<IFileSystemService>();
        var logger = Substitute.For<ILogger<BackupRestoreService>>();
        _service = new BackupRestoreService(_pathConfig, fileSystem, logger);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        foreach (var file in _tempFiles)
            if (File.Exists(file)) File.Delete(file);
        GC.SuppressFinalize(this);
    }

    // -------------------------------------------------------------------------
    // ValidateBackupAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValidateBackupAsync_WhenFileDoesNotExist_ReturnsFalse()
    {
        var result = await _service.ValidateBackupAsync(@"C:\nonexistent\backup.zip");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateBackupAsync_WithZipContainingNagiDb_ReturnsTrue()
    {
        var zipPath = CreateTempZip(("nagi.db", "database content"));

        var result = await _service.ValidateBackupAsync(zipPath);

        result.Should().BeTrue("a ZIP containing nagi.db is a valid Nagi backup");
    }

    [Fact]
    public async Task ValidateBackupAsync_WithZipContainingOnlySettingsJson_ReturnsTrue()
    {
        var zipPath = CreateTempZip(("settings.json", "{ }"));

        var result = await _service.ValidateBackupAsync(zipPath);

        result.Should().BeTrue("a ZIP containing settings.json alone should be considered a valid backup");
    }

    [Fact]
    public async Task ValidateBackupAsync_WithZipMissingBothDbAndSettings_ReturnsFalse()
    {
        var zipPath = CreateTempZip(("some_other_file.txt", "irrelevant"));

        var result = await _service.ValidateBackupAsync(zipPath);

        result.Should().BeFalse("a backup missing both nagi.db and settings.json should be rejected");
    }

    [Fact]
    public async Task ValidateBackupAsync_WithCorruptOrNonZipFile_ReturnsFalse()
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x02, 0x03]); // not a ZIP

        var result = await _service.ValidateBackupAsync(path);

        result.Should().BeFalse("a corrupt or non-ZIP file must not pass validation");
    }

    // -------------------------------------------------------------------------
    // CreateBackupAsync
    // -------------------------------------------------------------------------

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
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "nagi.db"), "db data");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "settings.json"), "{ }");
        _pathConfig.AppDataRoot.Returns(sourceDir);

        var result = await _service.CreateBackupAsync(destDir);

        result.Success.Should().BeTrue();
        result.BackupFilePath.Should().NotBeNull();
        File.Exists(result.BackupFilePath!).Should().BeTrue("the ZIP file should be created at the reported path");

        using var archive = ZipFile.OpenRead(result.BackupFilePath!);
        archive.Entries.Should().Contain(e => e.FullName == "nagi.db");
        archive.Entries.Should().Contain(e => e.FullName == "settings.json");
    }

    [Fact]
    public async Task CreateBackupAsync_ExcludesLogsDirectory()
    {
        var sourceDir = CreateTempDir();
        var destDir = CreateTempDir();
        var logsDir = Directory.CreateDirectory(Path.Combine(sourceDir, "Logs"));
        await File.WriteAllTextAsync(Path.Combine(logsDir.FullName, "app.log"), "logs");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "settings.json"), "{ }");
        _pathConfig.AppDataRoot.Returns(sourceDir);

        var result = await _service.CreateBackupAsync(destDir);

        result.Success.Should().BeTrue();
        using var archive = ZipFile.OpenRead(result.BackupFilePath!);
        archive.Entries.Should().NotContain(e => e.FullName.StartsWith("Logs/", StringComparison.OrdinalIgnoreCase),
            "the Logs directory is explicitly excluded from backups");
    }

    [Fact]
    public async Task CreateBackupAsync_ExcludesTemporaryAndLockFiles()
    {
        var sourceDir = CreateTempDir();
        var destDir = CreateTempDir();
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "settings.json"), "{ }");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "temp.tmp"), "tmp");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "data.lock"), "lock");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "nagi.db-wal"), "wal");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "nagi.db-shm"), "shm");
        _pathConfig.AppDataRoot.Returns(sourceDir);

        var result = await _service.CreateBackupAsync(destDir);

        result.Success.Should().BeTrue();
        using var archive = ZipFile.OpenRead(result.BackupFilePath!);
        archive.Entries.Should().NotContain(e => e.FullName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        archive.Entries.Should().NotContain(e => e.FullName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
        archive.Entries.Should().NotContain(e => e.FullName.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase));
        archive.Entries.Should().NotContain(e => e.FullName.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateBackupAsync_ExcludesCacheDumpAndEBWebViewDirectories()
    {
        var sourceDir = CreateTempDir();
        var destDir = CreateTempDir();
        foreach (var dirName in new[] { "Cache", "Temp", "EBWebView", "crashdumps" })
        {
            var dir = Directory.CreateDirectory(Path.Combine(sourceDir, dirName));
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "file.txt"), "content");
        }
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "settings.json"), "{ }");
        _pathConfig.AppDataRoot.Returns(sourceDir);

        var result = await _service.CreateBackupAsync(destDir);

        result.Success.Should().BeTrue();
        using var archive = ZipFile.OpenRead(result.BackupFilePath!);
        foreach (var excluded in new[] { "Cache/", "Temp/", "EBWebView/", "crashdumps/" })
            archive.Entries.Should().NotContain(e => e.FullName.StartsWith(excluded, StringComparison.OrdinalIgnoreCase),
                $"{excluded} should be excluded from backups");
    }

    [Fact]
    public async Task CreateBackupAsync_ExcludesLibVlcCacheVersionFile()
    {
        var sourceDir = CreateTempDir();
        var destDir = CreateTempDir();
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "settings.json"), "{ }");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, ".libvlc-cache-version"), "1");
        _pathConfig.AppDataRoot.Returns(sourceDir);

        var result = await _service.CreateBackupAsync(destDir);

        result.Success.Should().BeTrue();
        using var archive = ZipFile.OpenRead(result.BackupFilePath!);
        archive.Entries.Should().NotContain(e => e.FullName.Equals(".libvlc-cache-version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateBackupAsync_IncludesNonExcludedSubdirectories()
    {
        var sourceDir = CreateTempDir();
        var destDir = CreateTempDir();
        var albumArtDir = Directory.CreateDirectory(Path.Combine(sourceDir, "AlbumArt"));
        await File.WriteAllTextAsync(Path.Combine(albumArtDir.FullName, "cover.jpg"), "fake image");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "settings.json"), "{ }");
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
        var subDirContent = "content";
        var zipPath = Path.GetTempFileName() + ".zip";
        _tempFiles.Add(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var dbEntry = zip.CreateEntry("nagi.db");
            using (var w = new StreamWriter(dbEntry.Open()))
                w.Write("db");

            var subEntry = zip.CreateEntry("AlbumArt/cover.jpg");
            using (var w2 = new StreamWriter(subEntry.Open()))
                w2.Write(subDirContent);
        }

        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(destDir, "AlbumArt")).Should().BeTrue("subdirectory should be restored");
        File.Exists(Path.Combine(destDir, "AlbumArt", "cover.jpg")).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // RestoreFromBackupAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RestoreFromBackupAsync_WithInvalidBackupFile_ReturnsFailure()
    {
        // A ZIP without nagi.db or settings.json is not a valid backup.
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
        var zipPath = CreateTempZip(
            ("nagi.db", "restored db content"),
            ("settings.json", "restored settings"));
        var destDir = CreateTempDir();
        _pathConfig.AppDataRoot.Returns(destDir);

        var result = await _service.RestoreFromBackupAsync(zipPath);

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(destDir, "nagi.db")).Should().BeTrue("nagi.db should be restored to AppDataRoot");
        File.Exists(Path.Combine(destDir, "settings.json")).Should().BeTrue("settings.json should be restored to AppDataRoot");
        var restoredDbContent = await File.ReadAllTextAsync(Path.Combine(destDir, "nagi.db"));
        restoredDbContent.Should().Be("restored db content");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_WhenLaterItemIsLocked_RollsBackEveryEarlierItem()
    {
        var zipPath = CreateTempZip(
            ("a.txt", "new first item"),
            ("settings.json", "new locked item"));
        var destDir = CreateTempDir();
        var firstPath = Path.Combine(destDir, "a.txt");
        var lockedPath = Path.Combine(destDir, "settings.json");
        await File.WriteAllTextAsync(firstPath, "original first item");
        await File.WriteAllTextAsync(lockedPath, "original locked item");
        _pathConfig.AppDataRoot.Returns(destDir);

        RestoreResult result;
        using (File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await _service.RestoreFromBackupAsync(zipPath);
        }

        result.Success.Should().BeFalse();
        result.RestoredFileCount.Should().Be(0);
        (await File.ReadAllTextAsync(firstPath)).Should().Be("original first item");
        (await File.ReadAllTextAsync(lockedPath)).Should().Be("original locked item");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private string CreateTempZip(params (string name, string content)[] entries)
    {
        var path = Path.GetTempFileName() + ".zip";
        _tempFiles.Add(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }
}
