using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Backup;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Scrobbling;
using Nagi.Core.Settings;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests.Backup;

public class AppDataResetTests : IDisposable
{
    private readonly PathConfiguration _paths = new(Directory.CreateTempSubdirectory("Nagi-reset-").FullName);
    private readonly Dictionary<string, object> _storedSettings = new() { ["AppVolume"] = 0.5 };
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IScrobblingAccounts _accounts = Substitute.For<IScrobblingAccounts>();
    private readonly IAutoLaunchService _autoLaunch = Substitute.For<IAutoLaunchService>();
    private readonly ILibraryReset _libraryReset = Substitute.For<ILibraryReset>();
    private readonly AppDataReset _reset;

    public AppDataResetTests()
    {
        var store = Substitute.For<ISettingsStore>();
        store.Values.Returns(_storedSettings);
        _reset = new AppDataReset(_playback, _settings, _accounts, _autoLaunch, _libraryReset, store, _paths,
            new FileSystemService(), NullLogger<AppDataReset>.Instance);

        File.WriteAllText(_paths.PlaybackStateFilePath, "{}");
        foreach (var folder in _paths.LibraryCacheFolders.Append(_paths.RomanizationPacksPath))
            File.WriteAllText(Path.Combine(folder, "cached"), "data");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_paths.AppDataRoot, true);
    }

    [Fact]
    public async Task Reset_ClearsTheQueueAccountsSettingsAndLibrary_AndDeletesTheSessionAndPacks()
    {
        await _reset.ResetAsync();

        Received.InOrder(() =>
        {
            _playback.ClearQueueAsync();
            _accounts.DisconnectLastFmAsync();
            _accounts.DisconnectListenBrainzAsync();
            _settings.ResetToDefaults();
            _autoLaunch.SetEnabledAsync(false);
            _libraryReset.ClearAllLibraryDataAsync();
        });
        File.Exists(_paths.PlaybackStateFilePath).Should().BeFalse();
        Directory.Exists(_paths.RomanizationPacksPath).Should().BeFalse();
    }

    [Fact]
    public async Task Reset_WhenTheServicesFail_DeletesEveryDataFileButTheLogs()
    {
        _libraryReset.ClearAllLibraryDataAsync().ThrowsAsync(new IOException("locked"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _paths.DatabasePath, Pooling = true }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE SavedData (Value TEXT); INSERT INTO SavedData VALUES ('old');";
            command.ExecuteNonQuery();
        }
        File.WriteAllText(_paths.DatabasePath + "-journal", "old rollback journal");

        await _reset.ResetAsync();

        _storedSettings.Should().BeEmpty();
        Directory.GetFileSystemEntries(_paths.AppDataRoot).Should().Equal(_paths.LogsDirectory);
    }

    [Fact]
    public async Task Reset_WhenTheDatabaseCannotBeDeleted_KeepsItsJournals()
    {
        _libraryReset.ClearAllLibraryDataAsync().ThrowsAsync(new IOException("locked"));
        File.WriteAllText(_paths.DatabasePath, "locked database");
        var journals = new[] { "-wal", "-shm", "-journal" };
        foreach (var suffix in journals) File.WriteAllText(_paths.DatabasePath + suffix, suffix);

        using (File.Open(_paths.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None))
            await _reset.ResetAsync();

        File.ReadAllText(_paths.DatabasePath).Should().Be("locked database");
        foreach (var suffix in journals) File.ReadAllText(_paths.DatabasePath + suffix).Should().Be(suffix);
    }
}
