using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;

namespace Nagi.Presentation.Services;

/// <summary>
///     Starts and stops the app's services in the order they depend on each other. Every UI head calls these
///     once its service provider is built and when its main window closes.
/// </summary>
public static class AppServices
{
    public static async Task StartAsync(IServiceProvider services, bool restoreSession, ILogger logger)
    {
        logger.LogDebug("Starting core services initialization.");

        // Pre-warm the audio player on a background thread.
        // This runs concurrently with database/window init so LibVLC is ready when user presses play.
        _ = Task.Run(async () =>
        {
            try
            {
                await services.GetRequiredService<IAudioPlayer>().EnsureInitializedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Audio player warm-up failed. Will initialize on first playback.");
            }
        });

        var dbTask = InitializeDatabaseAsync(services);
        var windowTask = services.GetRequiredService<IWindowService>().InitializeAsync();

        // Start non-EF services while database migration is still running.
        await windowTask;

        var presenceTask = services.GetRequiredService<IPresenceManager>().InitializeAsync();
        var trayTask = services.GetRequiredService<TrayIconViewModel>().InitializeAsync();
        var appInfoTask = services.GetRequiredService<IAppInfoService>().InitializeAsync();

        // playback and settings both need EF ready. LoadSettingsAsync also reads
        // CurrentEqualizerSettings from the playback service, so settings starts after playback.
        // Offline scrobbling queries ListenHistory, including columns added by recent migrations,
        // so start it only after database migration/backfill has completed.
        await dbTask;
        var offlineScrobbleService = services.GetRequiredService<IOfflineScrobbleService>();
        offlineScrobbleService.Start();
        var scrobbleTask = offlineScrobbleService.ProcessQueueAsync();
        var playbackTask = services.GetRequiredService<IMusicPlaybackService>().InitializeAsync(restoreSession);
        await playbackTask;
        var settingsTask = services.GetRequiredService<SettingsViewModel>().LoadSettingsAsync();

        await Task.WhenAll(presenceTask, trayTask, scrobbleTask, appInfoTask, settingsTask);

        logger.LogInformation("Core services initialized successfully.");
    }

    /// <summary>
    ///     Saves the playback session and finalizes listen history. The caller disposes the provider afterwards.
    /// </summary>
    public static async Task StopAsync(IServiceProvider services, ILogger logger)
    {
        logger.LogInformation("Shutting down services.");

        services.GetRequiredService<PlayerViewModel>().Cleanup();
        services.GetRequiredService<TrayIconViewModel>().Cleanup();

        var presenceTask = services.GetRequiredService<IPresenceManager>().ShutdownAsync();
        var saveStateTask = SaveSessionAsync(services, logger);

        await Task.WhenAll(presenceTask, saveStateTask);

        // Finalize listen history before disposing the database service provider.
        await services.GetRequiredService<IMusicPlaybackService>().DisposeAsync();
    }

    /// <summary>
    ///     Saves the playback session for the next launch, or clears it when the user turned restoring off.
    /// </summary>
    private static async Task SaveSessionAsync(IServiceProvider services, ILogger logger)
    {
        var settingsService = services.GetRequiredService<ISettingsService>();
        var musicPlaybackService = services.GetRequiredService<IMusicPlaybackService>();

        try
        {
            if (await settingsService.GetRestorePlaybackStateEnabledAsync())
                await musicPlaybackService.SavePlaybackStateAsync();
            else
                await settingsService.ClearPlaybackStateAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error saving application state.");
        }
    }

    private static Task InitializeDatabaseAsync(IServiceProvider services) =>
        Task.Run(async () =>
        {
            var dbContextFactory = services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            await using var dbContext = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            await dbContext.Database.MigrateAsync().ConfigureAwait(false);
            await dbContext.BackfillSortKeysAsync().ConfigureAwait(false);
        });
}
