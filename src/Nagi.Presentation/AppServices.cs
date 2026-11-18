using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Presence;
using Nagi.Core.Scrobbling;
using Nagi.Presentation.MusicServers;
using Nagi.Presentation.Player;
using Nagi.Presentation.Settings;
using Nagi.Presentation.Shell;

namespace Nagi.Presentation;

/// <summary>
///     Starts and stops the app's services in the order they depend on each other. Every UI head calls these
///     once its service provider is built, once it shows its main content, and when its main window closes.
/// </summary>
public static class AppServices
{
    public static async Task StartAsync(IServiceProvider services, Task databaseReady, bool restoreSession,
        ILogger logger)
    {
        logger.LogDebug("Starting core services initialization.");

        // Start non-EF services while database migration is still running.
        services.GetRequiredService<DiscordPresenceService>().Start();
        services.GetRequiredService<TrayIconViewModel>().Initialize();
        var equalizerTask = services.GetRequiredService<IEqualizerService>().InitializeAsync();

        // Playback and settings both need EF ready. Scrobbling queries ListenHistory, including columns
        // added by recent migrations, so start it only after database migration/backfill has completed.
        await databaseReady;
        services.GetRequiredService<ScrobbleCoordinator>().Start();
        // Off this thread: each one's first query builds its plan before it first yields.
        _ = Task.Run(services.GetRequiredService<LibraryFolderWatcher>().Start);
        _ = Task.Run(services.GetRequiredService<ILibraryFolderService>().RefreshOnStartupAsync);
        var playbackTask = services.GetRequiredService<IMusicPlaybackService>().InitializeAsync(restoreSession);
        var settingsTask = services.GetRequiredService<SettingsViewModel>().LoadSettingsAsync();

        await Task.WhenAll(equalizerTask, playbackTask, settingsTask);

        logger.LogInformation("Core services initialized successfully.");
    }

    /// <summary>
    ///     Starts what has to wait for the main content: music server syncs and the prompts they can show.
    /// </summary>
    public static void StartAfterMainContent(IServiceProvider services) =>
        services.GetRequiredService<MusicServerPrompts>().Start();

    /// <summary>
    ///     Saves the playback session and finalizes listen history. The caller disposes the provider afterwards.
    /// </summary>
    public static async Task StopAsync(IServiceProvider services, ILogger logger)
    {
        logger.LogInformation("Shutting down services.");

        services.GetRequiredService<PlayerViewModel>().Cleanup();
        services.GetRequiredService<TrayIconViewModel>().Cleanup();

        services.GetRequiredService<DiscordPresenceService>().Stop();
        await SaveSessionAsync(services, logger);

        // Finalize listen history before disposing the database service provider.
        await services.GetRequiredService<IMusicPlaybackService>().DisposeAsync();
    }

    private static async Task SaveSessionAsync(IServiceProvider services, ILogger logger)
    {
        try
        {
            await services.GetRequiredService<IMusicPlaybackService>().SavePlaybackStateAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error saving application state.");
        }
    }

    /// <summary>
    ///     Migrates the database in the background. A head starts this as soon as its provider is built, so the
    ///     migration runs while it creates its window.
    /// </summary>
    public static Task StartDatabase(IServiceProvider services) =>
        Task.Run(async () =>
        {
            var dbContextFactory = services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            await using var dbContext = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            // Migrating takes a lock and a transaction even when there is nothing to apply.
            var pending = await dbContext.Database.GetPendingMigrationsAsync().ConfigureAwait(false);
            if (pending.Any()) await dbContext.Database.MigrateAsync().ConfigureAwait(false);
            await dbContext.BackfillSortKeysAsync().ConfigureAwait(false);
        });
}
