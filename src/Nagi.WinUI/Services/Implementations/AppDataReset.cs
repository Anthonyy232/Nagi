using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Serilog;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Wipes settings, the library database and caches when the user asks for a reset from the crash
///     dialog, when the app may be too broken to use its normal services.
/// </summary>
internal static class AppDataReset
{
    /// <summary>
    ///     Clears settings and library data through <paramref name="services" /> when they are available,
    ///     and deletes the files directly when they are not or when that fails.
    /// </summary>
    public static async Task ResetAsync(IServiceProvider? services)
    {
        var pathConfig = new PathConfiguration();

        if (services != null)
        {
            try
            {
                var settingsService = services.GetRequiredService<ISettingsService>();
                var libraryWriter = services.GetRequiredService<ILibraryWriter>();

                // Use robust service methods first
                await settingsService.ResetToDefaultsAsync();
                await libraryWriter.ClearAllLibraryDataAsync();
            }
            catch (Exception serviceEx)
            {
                Log.Warning(serviceEx, "Service-based reset failed. Falling back to manual file deletion.");
                PerformManualFileReset(pathConfig);
            }
        }
        else
        {
            // Fallback: Manually delete files if DI hasn't initialized
            Log.Warning("Services not available during reset. Performing manual file deletion.");
            PerformManualFileReset(pathConfig);
        }
    }

    private static void PerformManualFileReset(IPathConfiguration pathConfig)
    {
        try
        {
            // Packaged settings live in LocalSettings; legacy state files are cleared below.
            try
            {
                ApplicationData.Current.LocalSettings.Values.Clear();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to clear LocalSettings.");
            }

            SafelyDeleteFile(pathConfig.SettingsFilePath);
            SafelyDeleteFile(pathConfig.PlaybackStateFilePath);

            DeleteDatabaseFiles(pathConfig.DatabasePath);

            SafelyDeleteDir(pathConfig.AlbumArtCachePath);
            SafelyDeleteDir(pathConfig.ArtistImageCachePath);
            SafelyDeleteDir(pathConfig.PlaylistImageCachePath);
            SafelyDeleteDir(pathConfig.LrcCachePath);
            SafelyDeleteDir(pathConfig.RomanizationPacksPath);

            Log.Information("Manual file reset completed.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Critical failure during manual file reset.");
        }
    }

    internal static void DeleteDatabaseFiles(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        // Keep the journal with its database if an active connection still holds the file open.
        if (!SafelyDeleteFile(databasePath)) return;
        SafelyDeleteFile(databasePath + "-wal");
        SafelyDeleteFile(databasePath + "-shm");
        SafelyDeleteFile(databasePath + "-journal");
    }

    private static bool SafelyDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete file: {Path}", path);
            return false;
        }
    }

    private static void SafelyDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete directory: {Path}", path);
        }
    }
}
