using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Scrobbling;
using Nagi.Core.Settings;

namespace Nagi.Core.Backup;

public sealed class AppDataReset : IAppDataReset
{
    private readonly IMusicPlaybackService _playback;
    private readonly ISettingsService _settings;
    private readonly IScrobblingAccounts _accounts;
    private readonly IAutoLaunchService _autoLaunch;
    private readonly ILibraryReset _libraryReset;
    private readonly ISettingsStore _settingsStore;
    private readonly IPathConfiguration _paths;
    private readonly IFileSystemService _fileSystem;
    private readonly ILogger<AppDataReset> _logger;

    public AppDataReset(IMusicPlaybackService playback, ISettingsService settings, IScrobblingAccounts accounts,
        IAutoLaunchService autoLaunch, ILibraryReset libraryReset, ISettingsStore settingsStore, IPathConfiguration paths,
        IFileSystemService fileSystem, ILogger<AppDataReset> logger)
    {
        _playback = playback;
        _settings = settings;
        _accounts = accounts;
        _autoLaunch = autoLaunch;
        _libraryReset = libraryReset;
        _settingsStore = settingsStore;
        _paths = paths;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task ResetAsync()
    {
        _logger.LogInformation("Resetting all application data.");
        try
        {
            // The session is saved again at shutdown, so it has to be empty by then.
            await _playback.ClearQueueAsync().ConfigureAwait(false);
            await _accounts.DisconnectLastFmAsync().ConfigureAwait(false);
            await _accounts.DisconnectListenBrainzAsync().ConfigureAwait(false);
            _settings.ResetToDefaults();
            await _autoLaunch.SetEnabledAsync(false).ConfigureAwait(false);
            await _libraryReset.ClearAllLibraryDataAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resetting through the services failed. Deleting the data files instead.");
            DeleteFilesDirectly();
        }

        TryDelete(_paths.PlaybackStateFilePath);
        TryDelete(_paths.RomanizationPacksPath);
    }

    private void DeleteFilesDirectly()
    {
        try
        {
            _settingsStore.Values.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the settings store during the reset.");
        }

        SqliteConnection.ClearAllPools();
        // Keep the journals with their database if a connection still holds it open.
        if (TryDelete(_paths.DatabasePath))
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                TryDelete(_paths.DatabasePath + suffix);

        foreach (var folder in _paths.LibraryCacheFolders) TryDelete(folder);
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (_fileSystem.DirectoryExists(path)) _fileSystem.DeleteDirectory(path, true);
            else _fileSystem.DeleteFile(path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path} during the reset.", path);
            return false;
        }
    }
}
