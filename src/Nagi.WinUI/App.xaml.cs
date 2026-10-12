using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;
using Nagi.WinUI.ViewModels;
using Serilog;
using Serilog.Events;
using WinRT;
using LaunchActivatedEventArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace Nagi.WinUI;

/// <summary>
///     Provides application-specific behavior, manages the application lifecycle,
///     and configures dependency injection.
/// </summary>
public partial class App : Application
{
    private static string? _currentLogFilePath;

    private readonly ConcurrentQueue<string> _fileActivationQueue = new();
    private bool _isProcessingFileQueue;
    private bool _coreServicesInitialized;
    private volatile bool _hasExternalActivation;
    private bool _isReportingCrash;
    private ILogger<App>? _logger;
    private Window? _window;

    public App()
    {
        CurrentApp = this;
        InitializeComponent();

        UnhandledException += OnAppUnhandledException;
    }

    public static App? CurrentApp { get; private set; }

    /// <summary>
    ///     Gets the main application window.
    /// </summary>
    public static Window? RootWindow => CurrentApp?._window;

    /// <summary>
    ///     Gets the configured service provider.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    /// <summary>
    ///     Gets the dispatcher queue for the main UI thread.
    /// </summary>
    public static DispatcherQueue? MainDispatcherQueue => CurrentApp?._window?.DispatcherQueue;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();
        var tempPathConfig = new PathConfiguration();

        _currentLogFilePath = Path.Combine(tempPathConfig.LogsDirectory, "log.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Migrations", LogEventLevel.Error)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Error)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .WriteTo.Debug()
            .WriteTo.File(_currentLogFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(MemoryLog.Instance)
            .CreateLogger();

        try
        {
            InitializeWindowAndServices(configuration);
            _logger = Services!.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("Application starting up.");

            InitializeSystemIntegration();

            // Set a Splash/Skeleton page immediately and restore layout
            if (_window is MainWindow mainWindow)
            {
                mainWindow.Content = new Pages.SplashPage();
                mainWindow.InitializeCustomTitleBar();
                await mainWindow.RestoreWindowStateAsync();
            }
            // Before doing heavy blocking initialization, show the window to the user
            var isStartupLaunch = Environment.GetCommandLineArgs()
                .Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            var uiSettings = Services!.GetRequiredService<IUISettingsService>();
            var startMinimized = await uiSettings.GetStartMinimizedEnabledAsync();

            if (!isStartupLaunch && !startMinimized)
            {
                _window?.AppWindow?.Show();
                _window?.Activate();
                if (_window?.AppWindow is not null) _window.AppWindow.IsShownInSwitchers = true;
            }
            HandleInitialActivation(args.UWPLaunchActivatedEventArgs);

            var restoreSession = _fileActivationQueue.IsEmpty;
            await InitializeCoreServicesAsync(Services!, restoreSession);
            if (_isExiting) return;
            _coreServicesInitialized = true;
            await CheckAndNavigateToMainContent();

            _ = ProcessFileActivationQueueAsync();

            await HandleWindowActivationAsync(isStartupLaunch);

            EnqueuePostLaunchTasks();
        }
        catch (Exception ex)
        {
            if (_isExiting)
            {
                Log.Debug(ex, "Startup interrupted by application shutdown.");
                return;
            }
            Log.Fatal(ex, "Application terminated unexpectedly during startup.");
            await ShowCrashReportAndExitAsync(ex);
        }
    }

    private void HandleInitialActivation(IActivatedEventArgs args)
    {
        string? filePath = null;

        if (args.Kind == ActivationKind.File)
        {
            var fileArgs = args.As<IFileActivatedEventArgs>();
            if (fileArgs.Files.Any()) filePath = fileArgs.Files[0].Path;
        }
        else if (args.Kind == ActivationKind.Launch)
        {
            filePath = Program.TryGetFilePathFromArgs(Environment.GetCommandLineArgs()[1..]);
        }

        if (!string.IsNullOrEmpty(filePath))
        {
            _hasExternalActivation = true;
            _fileActivationQueue.Enqueue(filePath);
        }
    }

    /// <summary>
    ///     Handles an activation request from an external source (e.g., a secondary instance).
    ///     This method orchestrates file queuing and window activation logic on the UI thread.
    /// </summary>
    /// <param name="filePath">The file path from the activation arguments, if any.</param>
    public void HandleExternalActivation(string? filePath)
    {
        _hasExternalActivation = true;
        if (!string.IsNullOrEmpty(filePath)) _fileActivationQueue.Enqueue(filePath);
        MainDispatcherQueue?.TryEnqueue(() =>
        {
            if (!_coreServicesInitialized || _isExiting) return;

            _ = ProcessFileActivationQueueAsync();

            try
            {
                var windowService = Services!.GetRequiredService<IWindowService>();

                // If a file path is provided and we are already in the mini-player, we just
                // play the file without disrupting the mini-player view.
                if (!string.IsNullOrEmpty(filePath) && windowService.IsMiniPlayerActive)
                {
                    return;
                }

                windowService.ShowAndActivate();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "HandleExternalActivation: Exception during window activation");
            }
        });
    }

    private async Task ProcessFileActivationQueueAsync()
    {
        if (!_coreServicesInitialized || _isProcessingFileQueue || _isExiting) return;

        _isProcessingFileQueue = true;
        try
        {
            while (!_isExiting && _fileActivationQueue.TryDequeue(out var filePath))
            {
                try
                {
                    await Services!.GetRequiredService<IMusicPlaybackService>().PlayTransientFileAsync(filePath);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to process file '{FilePath}'", filePath);
                }
            }
        }
        finally
        {
            _isProcessingFileQueue = false;
        }
    }

    private async Task InitializeCoreServicesAsync(IServiceProvider services, bool restoreSession)
    {
        _logger?.LogDebug("Starting core services initialization.");

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
                _logger?.LogWarning(ex, "Audio player warm-up failed. Will initialize on first playback.");
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

        _logger?.LogInformation("Core services initialized successfully.");
    }

    private static IServiceProvider ConfigureServices(DispatcherQueue dispatcherQueue, App appInstance,
        IConfiguration configuration)
    {
        var services = new ServiceCollection();

        services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog());
        services.AddSingleton(configuration);
        services.AddNagiHttpClients();
        services.AddNagiPlatformServices(dispatcherQueue, appInstance);
        services.AddNagiCore();
        services.AddNagiViewModels();

        return services.BuildServiceProvider();
    }

    private static Task InitializeDatabaseAsync(IServiceProvider services) =>
        Task.Run(async () =>
        {
            var dbContextFactory = services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            await using var dbContext = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            await dbContext.Database.MigrateAsync().ConfigureAwait(false);
            await dbContext.BackfillSortKeysAsync().ConfigureAwait(false);

        });

    private void InitializeWindowAndServices(IConfiguration configuration)
    {
        var mainWindow = new MainWindow();
        _window = mainWindow;
        _window.Closed += OnWindowClosed;

        Services = ConfigureServices(_window.DispatcherQueue, this, configuration);

        mainWindow.InitializeDependencies(Services.GetRequiredService<IUISettingsService>());
    }

    private void InitializeSystemIntegration()
    {
        try
        {
            _window!.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/AppLogo.ico"));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set window icon");
        }
    }

    private async Task SaveApplicationStateAsync(IServiceProvider services)
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
            _logger?.LogError(ex, "Unexpected error saving application state.");
        }
    }

    private bool _isExiting;

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // We need to handle this manually to ensure async tasks complete before the app exits.
        args.Handled = true;
        if (_isExiting) return;
        _isExiting = true;

        try
        {
            if (_window is MainWindow mainWindow)
            {
                _logger?.LogDebug("Saving window state...");
                await mainWindow.SaveWindowStateAsync();
                mainWindow.Cleanup();
            }

            if (Services is not null)
            {
                _logger?.LogInformation("Window is closing. Shutting down services.");

                Services.GetRequiredService<PlayerViewModel>().Cleanup();
                Services.GetRequiredService<TrayIconViewModel>().Cleanup();

                var presenceTask = Services.GetRequiredService<IPresenceManager>().ShutdownAsync();
                var saveStateTask = SaveApplicationStateAsync(Services);

                await Task.WhenAll(presenceTask, saveStateTask);

                // Finalize listen history before disposing the database service provider.
                await Services.GetRequiredService<IMusicPlaybackService>().DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during application shutdown.");
        }
        finally
        {
            try
            {
                if (Services is IAsyncDisposable asyncDisposableServices)
                    await asyncDisposableServices.DisposeAsync();
                else if (Services is IDisposable disposableServices)
                    disposableServices.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to dispose application services.");
            }
            finally
            {
                await ExitAsync(0);
            }
        }
    }

    private static async Task ExitAsync(int exitCode)
    {
        try
        {
            await Log.CloseAndFlushAsync();
        }
        finally
        {
            Environment.Exit(exitCode);
        }
    }

    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = ShowCrashReportAndExitAsync(e.Exception);
    }

    private async Task ShowCrashReportAndExitAsync(Exception ex)
    {
        if (_isReportingCrash) return;
        _isReportingCrash = true;
        try
        {
            Log.Fatal(ex, "A critical error occurred. Application will now terminate. Log path: {LogPath}",
                _currentLogFilePath);
            var fullCrashReport = $"{MemoryLog.Instance.GetContent()}\n\n--- EXCEPTION DETAILS ---\n{ex}";
            if (MainDispatcherQueue is not { } dispatcher) return;

            var dispatcherService = Services?.GetService<IDispatcherService>() ?? new DispatcherService(dispatcher);
            await dispatcherService.EnqueueAsync(async () =>
            {
                if (Services?.GetService<IUIService>() is not { } uiService) return;
                var result = await uiService.ShowCrashReportDialogAsync(
                    Nagi.WinUI.Resources.Strings.CrashReport_Title,
                    Nagi.WinUI.Resources.Strings.CrashReport_Message,
                    fullCrashReport,
                    "https://github.com/Anthonyy232/Nagi/issues");

                if (result == CrashReportResult.Reset) await ResetApplicationDataAsync();
            });
        }
        catch (Exception dialogEx)
        {
            Debug.WriteLine($"Failed to show the crash report dialog: {dialogEx}");
        }
        finally
        {
            await ExitAsync(1);
        }
    }

    private async Task ResetApplicationDataAsync()
    {
        try
        {
            Log.Information("User requested a full application data reset from the crash dialog.");

            var pathConfig = new PathConfiguration();

            if (Services != null)
            {
                try
                {
                    var settingsService = Services.GetRequiredService<ISettingsService>();
                    var libraryWriter = Services.GetRequiredService<ILibraryWriter>();

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

            Log.Information("Reset complete. Restarting application.");
            await Log.CloseAndFlushAsync();

            // Restart the app cleanly
            ElevationHelper.RestartWithoutElevation();

        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Critical failure during app reset: {ex}");
            Log.Fatal(ex, "Critical failure during app reset.");
        }
        finally
        {
            await ExitAsync(0);
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

    /// <summary>
    ///     Sets the initial page of the application based on whether a music library has been configured.
    /// </summary>
    public async Task CheckAndNavigateToMainContent()
    {
        if (_isExiting || RootWindow is null || Services is null) return;

        var libraryReader = Services.GetRequiredService<ILibraryReader>();
        var hasFolders = await libraryReader.HasAnyFolderAsync();
        if (_isExiting) return;
        if (hasFolders)
        {
            if (RootWindow.Content is not MainPage) RootWindow.Content = new MainPage();
        }
        else
        {
            if (RootWindow.Content is not OnboardingPage) RootWindow.Content = new OnboardingPage();
        }
        if (RootWindow is MainWindow mainWindow)
        {
            var settingsService = Services.GetRequiredService<IUISettingsService>();
            var themeService = Services.GetRequiredService<IThemeService>();
            var savedTheme = await settingsService.GetThemeAsync();
            themeService.ApplyTheme(savedTheme);

            mainWindow.NotifyContentLoaded();
            mainWindow.InitializeCustomTitleBar();
        }
    }

    internal void ApplyThemeInternal(ElementTheme themeToApply)
    {
        EnsureOnUIThread(() =>
        {
            if (RootWindow?.Content is not FrameworkElement rootElement) return;

            rootElement.RequestedTheme = themeToApply;
            Services?.GetRequiredService<IThemeService>().ReapplyCurrentDynamicThemeAsync();

            if (RootWindow is MainWindow mainWindow) mainWindow.InitializeCustomTitleBar();
        }, nameof(ApplyThemeInternal));
    }

    public void SetAppPrimaryColorBrushColor(Color newColor)
    {
        SetSolidColorBrushColor(
            "AppPrimaryColorBrush",
            newColor,
            nameof(SetAppPrimaryColorBrushColor),
            refreshTaskbarIcons: true);
    }

    public void SetPlayerColors(Color tintColor, Color backgroundColor, Color accentColor)
    {
        SetSolidColorBrushColor("PlayerTintColorBrush", tintColor, nameof(SetPlayerColors));
        SetSolidColorBrushColor("PlayerBackgroundBrush", backgroundColor, nameof(SetPlayerColors));
        SetSolidColorBrushColor("PlayerAccentBrush", accentColor, nameof(SetPlayerColors));
    }

    public void SetMediaOnImageAccentBrushColor(Color newColor)
    {
        SetSolidColorBrushColor("MediaOnImageAccentBrush", newColor, nameof(SetMediaOnImageAccentBrushColor));
    }

    private void SetSolidColorBrushColor(
        string resourceKey,
        Color newColor,
        string methodName,
        bool refreshTaskbarIcons = false)
    {
        EnsureOnUIThread(() =>
        {
            if (Resources.TryGetValue(resourceKey, out var brushObject) &&
                brushObject is SolidColorBrush brush)
            {
                if (brush.Color == newColor) return;

                brush.Color = newColor;
                if (refreshTaskbarIcons)
                {
                    _ = (RootWindow as MainWindow)?.TaskbarService?.RefreshIconsAsync();
                }
            }
            else
            {
                _logger?.LogCritical("{ResourceKey} resource not found", resourceKey);
            }
        }, methodName);
    }

    private void EnsureOnUIThread(Action action, string methodName)
    {
        if (RootWindow?.DispatcherQueue is { } dispatcher)
        {
            if (dispatcher.HasThreadAccess)
            {
                action();
            }
            else
            {
                dispatcher.TryEnqueue(action.Invoke);
            }
        }
        else if (RootWindow != null)
        {
            _logger?.LogWarning("{Method}: RootWindow exists but DispatcherQueue is null. Skipping update.", methodName);
        }
        else
        {
            _logger?.LogDebug("{Method}: RootWindow is null. Skipping update.", methodName);
        }
    }

    internal static bool TryParseHexColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)) return false;

        if (hex.Length == 8) // AARRGGBB
        {
            color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }

        if (hex.Length == 6) // RRGGBB
        {
            color = Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }

        return false;
    }

    private async Task HandleWindowActivationAsync(bool isStartupLaunch = false)
    {
        if (_isExiting || _window is null || Services is null) return;

        var settingsService = Services.GetRequiredService<IUISettingsService>();
        var startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
        var minimizeToMiniPlayer = await settingsService.GetMinimizeToMiniPlayerEnabledAsync();
        var hideToTray = await settingsService.GetHideToTrayEnabledAsync();
        if (_isExiting) return;
        var windowService = Services.GetRequiredService<IWindowService>();

        // A launch received during initialization takes precedence over startup minimization.
        if (_hasExternalActivation || !(isStartupLaunch || startMinimized))
        {
            windowService.ShowAndActivate();
        }
        else if (minimizeToMiniPlayer)
        {
            windowService.ShowMiniPlayer();
            _window.AppWindow.IsShownInSwitchers = false;
        }
        else if (!hideToTray)
        {
            WindowActivator.ShowMinimized(_window);
        }
    }

    private void EnqueuePostLaunchTasks()
    {
        MainDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                if (_isExiting || Services is null) return;

                var audioPlayerService = Services.GetRequiredService<IAudioPlayer>();
                audioPlayerService.InitializeSmtc();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize System Media Transport Controls");
            }
        });
    }

}
