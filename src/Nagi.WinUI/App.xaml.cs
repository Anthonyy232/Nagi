using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.UI;
using Microsoft.EntityFrameworkCore;
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
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Services.Implementations.Presence;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;
using Nagi.WinUI.ViewModels;
using Serilog;
using Serilog.Events;
using WinRT;
using LaunchActivatedEventArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;


#if !MSIX_PACKAGE
using Velopack;
using Velopack.Sources;
#endif

namespace Nagi.WinUI;

/// <summary>
///     Provides application-specific behavior, manages the application lifecycle,
///     and configures dependency injection.
/// </summary>
public partial class App : Application
{
    private static Color? _systemAccentColor;
    private static string? _currentLogFilePath;

    private readonly ConcurrentQueue<string> _fileActivationQueue = new();
    private volatile bool _isProcessingFileQueue;
    private ILogger<App>? _logger;
    private Window? _window;

    // Current version of LibVLC.Windows package - update this when upgrading LibVLC
    private const string LibVlcVersion = "4.0.0-alpha-20250725";

    public App()
    {
        CurrentApp = this;
        InitializeComponent();
        
        UnhandledException += OnAppUnhandledException;
        CoreApplication.Suspending += OnSuspending;
    }

    /// <summary>
    ///     Generates the LibVLC plugin cache if it doesn't exist or if the LibVLC version has changed.
    ///     This dramatically improves LibVLC initialization time on subsequent launches.
    ///     Note: This is skipped for packaged (MSIX) builds as the installation directory is read-only.
    /// </summary>
    private static void EnsureLibVlcPluginCache()
    {
        // Skip for packaged builds - the installation directory is read-only in MSIX
        if (PathConfiguration.IsRunningInPackage())
        {
            return;
        }

        try
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nagi");

            var cacheMarkerPath = Path.Combine(appDataPath, ".libvlc-cache-version");

            // Check if we need to regenerate the cache
            var needsRegeneration = true;
            if (File.Exists(cacheMarkerPath))
            {
                var cachedVersion = File.ReadAllText(cacheMarkerPath).Trim();
                needsRegeneration = cachedVersion != LibVlcVersion;
            }

            if (needsRegeneration)
            {
                // We use Log.Information here because the Microsoft.Extensions.Logging system 
                // might not be fully wired up yet in the constructor sequence.
                Log.Information("Regenerating LibVLC plugin cache for version {Version}...", LibVlcVersion);

                // Generate the plugin cache - this creates plugins.dat in the libvlc plugins folder
                // Note: Core.Initialize() is already called by the audioWarmupTask before this runs
                using var tempVlc = new LibVLCSharp.LibVLC("--reset-plugins-cache");
                
                // Write the version marker so we don't regenerate unnecessarily
                Directory.CreateDirectory(appDataPath);
                File.WriteAllText(cacheMarkerPath, LibVlcVersion);
                
                Log.Information("LibVLC plugin cache generated successfully.");
            }
        }
        catch (Exception ex)
        {
            // Silently fail - plugin cache is an optimization, not required for functionality.
            // However, we log the failure if possible.
            Log.Warning(ex, "Failed to generate LibVLC plugin cache. This is non-critical but may affect startup performance.");
        }
    }


    /// <summary>
    ///     Gets the current running App instance.
    /// </summary>
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

    /// <summary>
    ///     Gets the system's current accent color, with a fallback.
    /// </summary>
    public static Color SystemAccentColor
    {
        get
        {
            _systemAccentColor ??= Current.Resources.TryGetValue("SystemAccentColor", out var value) &&
                                   value is Color color
                ? color
                : Colors.SlateGray;
            return _systemAccentColor.Value;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", false, true)
            .Build();
        var tempPathConfig = new PathConfiguration(configuration);

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

            // Restore window state BEFORE showing the window to prevent flash of default position
            if (_window is MainWindow mainWindow)
            {
                await mainWindow.RestoreWindowStateAsync();
            }

            HandleInitialActivation(args.UWPLaunchActivatedEventArgs);

            var restoreSession = _fileActivationQueue.IsEmpty;
            await InitializeCoreServicesAsync(restoreSession);

            await CheckAndNavigateToMainContent();

            ProcessFileActivationQueue();

            var isStartupLaunch = Environment.GetCommandLineArgs()
                .Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            await HandleWindowActivationAsync(isStartupLaunch);

            ShowElevationWarningIfNeededAsync();

            PerformPostLaunchTasks();
        }
        catch (Exception ex)
        {
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
            var commandLineArgs = Environment.GetCommandLineArgs();
            if (commandLineArgs.Length > 1) filePath = commandLineArgs[1];
        }

        if (!string.IsNullOrEmpty(filePath)) _fileActivationQueue.Enqueue(filePath);
    }

    /// <summary>
    ///     Queues a file path for playback. This can be called from external sources
    ///     (e.g., a single-instance redirection) to open files in the running application.
    /// </summary>
    public void EnqueueFileActivation(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        _fileActivationQueue.Enqueue(filePath);
        ProcessFileActivationQueue();
    }

    /// <summary>
    ///     Handles an activation request from an external source (e.g., a secondary instance).
    ///     This method orchestrates file queuing and window activation logic on the UI thread.
    /// </summary>
    /// <param name="filePath">The file path from the activation arguments, if any.</param>
    public void HandleExternalActivation(string? filePath)
    {
        MainDispatcherQueue?.TryEnqueue(() =>
        {
            if (_window is null)
            {
                _logger?.LogError("HandleExternalActivation: Aborted because the main window is not available");
                return;
            }

            if (!string.IsNullOrEmpty(filePath)) EnqueueFileActivation(filePath);

            try
            {
                var windowService = Services?.GetRequiredService<IWindowService>();
                var isMiniPlayerActive = windowService?.IsMiniPlayerActive ?? false;

                // If a file path is provided and we are already in the mini-player, we just
                // play the file without disrupting the mini-player view.
                if (!string.IsNullOrEmpty(filePath) && isMiniPlayerActive)
                {
                    return;
                }

                // Otherwise, always activate and show the main window on external activation,
                // whether it was triggered by a file play request or a simple app launch.
                if (windowService != null)
                {
                    // Use the WindowService to activate, which handles closing the mini-player
                    // and restoring task switcher visibility automatically.
                    windowService.ShowAndActivate();
                }
                else
                {
                    // Fallback if services aren't fully initialized (unlikely but possible).
                    _logger?.LogWarning(
                        "HandleExternalActivation: WindowService not available, using fallback activation");
                    _window.AppWindow.Show();
                    _window.Activate();
                    if (_window.AppWindow is not null) _window.AppWindow.IsShownInSwitchers = true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "HandleExternalActivation: Exception during window activation");
                _window.AppWindow.Show();
                _window.Activate();
                if (_window.AppWindow is not null) _window.AppWindow.IsShownInSwitchers = true;
            }
        });
    }

    private void ProcessFileActivationQueue()
    {
        if (_isProcessingFileQueue) return;

        MainDispatcherQueue?.TryEnqueue(async () =>
        {
            if (_isProcessingFileQueue) return;

            _isProcessingFileQueue = true;
            try
            {
                while (_fileActivationQueue.TryDequeue(out var filePath)) await ProcessFileActivationAsync(filePath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Exception while processing file activation queue");
            }
            finally
            {
                _isProcessingFileQueue = false;
            }
        });
    }

    public async Task ProcessFileActivationAsync(string filePath)
    {
        if (Services is null || string.IsNullOrEmpty(filePath))
        {
            _logger?.LogError("ProcessFileActivationAsync: Aborted due to null services or file path");
            return;
        }

        try
        {
            var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
            await playbackService.PlayTransientFileAsync(filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to process file '{FilePath}'", filePath);
        }
    }

    private async Task InitializeCoreServicesAsync(bool restoreSession = true)
    {
        if (Services is null) return;
        try
        {
            _logger?.LogDebug("Starting core services initialization.");

            // Pre-warm the audio player on a background thread.
            // This runs concurrently with database/window init so LibVLC is ready when user presses play.
            var audioWarmupTask = Task.Run(async () =>
            {
                try
                {
                    // Initialize LibVLC core first (registers native library paths)
                    LibVLCSharp.Core.Initialize();

                    // Ensure plugin cache exists BEFORE creating LibVLC instance
                    EnsureLibVlcPluginCache();
                    await Services.GetRequiredService<IAudioPlayer>().EnsureInitializedAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Audio player warm-up failed. Will initialize on first playback.");
                }
            });

            // 1. Foundation Phase: Initialize Database and WindowService in parallel.
            // These are independent and required for the next phase.
            var dbTask = InitializeDatabaseAsync(Services);
            var windowTask = Services.GetRequiredService<IWindowService>().InitializeAsync();
            await Task.WhenAll(dbTask, windowTask).ConfigureAwait(false);

            // 2. Services Phase: Parallelize independent service initializations.
            var playbackTask = Services.GetRequiredService<IMusicPlaybackService>().InitializeAsync(restoreSession);
            var presenceTask = Services.GetRequiredService<IPresenceManager>().InitializeAsync();
            var trayTask = Services.GetRequiredService<TrayIconViewModel>().InitializeAsync();

            var offlineScrobbleService = Services.GetRequiredService<IOfflineScrobbleService>();
            offlineScrobbleService.Start();
            var scrobbleTask = offlineScrobbleService.ProcessQueueAsync();

            // Wait for all non-dependent services to finish initializing.
            await Task.WhenAll(playbackTask, presenceTask, trayTask, scrobbleTask).ConfigureAwait(false);

            _logger?.LogInformation("Core services initialized successfully.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize core services");
        }
    }

    private static IServiceProvider ConfigureServices(Window window, DispatcherQueue dispatcherQueue, App appInstance,
        IConfiguration configuration)
    {
        var services = new ServiceCollection();

        services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog(dispose: true));

        services.AddSingleton(configuration);
        services.AddSingleton<IPathConfiguration, PathConfiguration>();
        services.AddHttpClient();
        
        // Configure the default HTTP client with standardized settings:
        // - 10s timeout to fail fast on unresponsive servers
        // - HTTP/1.1 forced for compatibility
        // - 30s connection lifetime, 15s idle timeout to prevent "Response Ended Prematurely"
        //   errors from servers closing idle connections that HttpClient tries to reuse
        services.AddHttpClient("")
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestVersion = HttpVersion.Version11;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Shorter lifetimes to avoid stale connections being reset by servers
                PooledConnectionLifetime = TimeSpan.FromSeconds(15),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
                // Fail fast if connection cannot be established
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });

        ConfigureAppSettingsServices(services);
        ConfigureCoreLogicServices(services);
        ConfigureWinUIServices(services, window, dispatcherQueue, appInstance);
        ConfigureViewModels(services);

        return services.BuildServiceProvider();
    }

    private static void ConfigureAppSettingsServices(IServiceCollection services)
    {
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<ICredentialLockerService, CredentialLockerService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IUISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
    }

    private static void ConfigureCoreLogicServices(IServiceCollection services)
    {
        services.AddDbContextFactory<MusicDbContext>((serviceProvider, options) =>
        {
            var pathConfig = serviceProvider.GetRequiredService<IPathConfiguration>();
            options.UseSqlite($"Data Source={pathConfig.DatabasePath}");
        });

        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(sp => sp.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryReader>(sp => sp.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryWriter>(sp => sp.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryScanner>(sp => sp.GetRequiredService<LibraryService>());
        services.AddSingleton<IPlaylistService>(sp => sp.GetRequiredService<LibraryService>());

        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<IOfflineScrobbleService, OfflineScrobbleService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IMetadataService, AtlMetadataService>();
        services.AddSingleton<ILrcService, LrcService>();

        services.AddSingleton<IApiKeyService, ApiKeyService>();
        services.AddSingleton<ILastFmMetadataService, LastFmMetadataService>();
        services.AddSingleton<ILastFmAuthService, LastFmAuthService>();
        services.AddSingleton<ISpotifyService, SpotifyService>();
        services.AddSingleton<IMusicBrainzService, MusicBrainzService>();
        services.AddSingleton<IFanartTvService, FanartTvService>();
        services.AddSingleton<ITheAudioDbService, TheAudioDbService>();
        services.AddSingleton<INetEaseLyricsService, NetEaseLyricsService>();
        services.AddSingleton<ILastFmScrobblerService, LastFmScrobblerService>();
        services.AddSingleton<IPresenceManager, PresenceManager>();
        services.AddSingleton<IPresenceService, DiscordPresenceService>();
        services.AddSingleton<IPresenceService, LastFmPresenceService>();
        services.AddSingleton<ISmartPlaylistService, SmartPlaylistService>();
        services.AddSingleton<IUpdateService, VelopackUpdateService>();
        services.AddSingleton<IOnlineLyricsService, LrcLibService>();
        services.AddSingleton<IPlaylistExportService, M3uPlaylistExportService>();
        
        // FFmpeg and ReplayGain services
        services.AddSingleton<IFFmpegService, FFmpegService>();
        services.AddSingleton<IPcmExtractor, FFmpegPcmExtractor>();
        services.AddSingleton<IReplayGainService, ReplayGainService>();

#if !MSIX_PACKAGE
        services.AddSingleton(_ =>
        {
            var source = new GithubSource("https://github.com/Anthonyy232/Nagi", null, false);
            return new UpdateManager(source);
        });
#endif
    }

    private static void ConfigureWinUIServices(IServiceCollection services, Window window,
        DispatcherQueue dispatcherQueue, App appInstance)
    {
        services.AddSingleton<IWin32InteropService, Win32InteropService>();
        services.AddSingleton<IWindowService>(sp => new WindowService(
            sp.GetRequiredService<IWin32InteropService>(),
            sp.GetRequiredService<IUISettingsService>(),
            sp.GetRequiredService<IDispatcherService>(),
            sp.GetRequiredService<ILogger<WindowService>>()
        ));
        services.AddSingleton<IUIService, UIService>();
        services.AddSingleton(dispatcherQueue);
        services.AddSingleton<IDispatcherService, DispatcherService>();
        services.AddSingleton<IThemeService>(sp =>
            new ThemeService(appInstance, sp, sp.GetRequiredService<ILogger<ThemeService>>()));
        services.AddSingleton<IApplicationLifecycle>(sp =>
            new ApplicationLifecycle(appInstance, sp, sp.GetRequiredService<ILogger<ApplicationLifecycle>>()));
        services.AddSingleton<IAppInfoService, AppInfoService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IMusicNavigationService, MusicNavigationService>();
        services.AddSingleton<ITrayPopupService, TrayPopupService>();
        services.AddSingleton<IAudioPlayer>(provider =>
            new LibVlcAudioPlayerService(provider.GetRequiredService<IDispatcherService>(),
                provider.GetRequiredService<ILogger<LibVlcAudioPlayerService>>()));
        services.AddTransient<ITaskbarService>(provider =>
            new TaskbarService(
                provider.GetRequiredService<ILogger<TaskbarService>>(),
                provider.GetRequiredService<IMusicPlaybackService>(),
                provider.GetRequiredService<IDispatcherService>(),
                provider.GetRequiredService<IWin32InteropService>()));
    }

    private static void ConfigureViewModels(IServiceCollection services)
    {
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<TrayIconViewModel>();

        services.AddTransient<LibraryViewModel>();
        services.AddTransient<PlaylistViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<PlaylistSongListViewModel>();
        services.AddTransient<SmartPlaylistSongListViewModel>();
        services.AddTransient<FolderViewModel>();
        services.AddTransient<FolderSongListViewModel>();
        services.AddTransient<ArtistViewModel>();
        services.AddTransient<ArtistViewViewModel>();
        services.AddTransient<AlbumViewViewModel>();
        services.AddTransient<AlbumViewModel>();
        services.AddTransient<GenreViewModel>();
        services.AddTransient<GenreViewViewModel>();
        services.AddTransient<LyricsPageViewModel>();
    }

    private static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        try
        {
            var dbContextFactory = services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            await using var dbContext = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            // Enable WAL mode for better concurrency and performance.
            // This is more reliable than setting it in the connection string for Microsoft.Data.Sqlite.
            await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;").ConfigureAwait(false);

            await dbContext.Database.MigrateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to initialize or migrate database.");
            throw; // Re-throw to propagate the failure to the startup handler.
        }
    }

    private void InitializeWindowAndServices(IConfiguration configuration)
    {
        try
        {
            _window = new MainWindow();
            _window.Closed += OnWindowClosed;

            Services = ConfigureServices(_window, _window.DispatcherQueue, this, configuration);

            if (_window is MainWindow mainWindow)
                mainWindow.InitializeDependencies(Services.GetRequiredService<IUISettingsService>());
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to initialize window and services.");
            throw;
        }
    }

    private void InitializeSystemIntegration()
    {
        try
        {
            var interopService = Services!.GetRequiredService<IWin32InteropService>();
            interopService.SetWindowIcon(_window!, "Assets/AppLogo.ico");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set window icon");
        }
    }

    private void PerformPostLaunchTasks()
    {
        _ = CheckForUpdatesOnStartupAsync();
        EnqueuePostLaunchTasks();
    }

    private async void OnSuspending(object? sender, SuspendingEventArgs e)
    {
        var deferral = e.SuspendingOperation.GetDeferral();
        try
        {
            if (Services is not null) await SaveApplicationStateAsync(Services);
        }
        catch (IOException ioEx)
        {
            _logger?.LogError(ioEx, "I/O error saving application state during suspension. User data may not be persisted.");
        }
        catch (UnauthorizedAccessException accessEx)
        {
            _logger?.LogError(accessEx, "Access denied saving application state during suspension. Check file permissions.");
        }
        catch (InvalidOperationException invalidOpEx)
        {
            _logger?.LogError(invalidOpEx, "Invalid operation during suspension. Services may already be disposed.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error during application suspension.");
        }
        finally
        {
            deferral.Complete();
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

            await settingsService.FlushAsync();
        }
        catch (IOException ioEx)
        {
            _logger?.LogError(ioEx, "I/O error saving application state. Settings file may be locked or disk full.");
        }
        catch (UnauthorizedAccessException accessEx)
        {
            _logger?.LogError(accessEx, "Access denied while saving application state. Check LocalAppData permissions.");
        }
        catch (ObjectDisposedException disposedEx)
        {
            _logger?.LogWarning(disposedEx, "Service already disposed during save. State may be partially saved.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error saving application state or flushing settings.");
        }
    }

    private bool _isExiting;

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_isExiting)
        {
            args.Handled = false;
            return;
        }

        _isExiting = true;
        
        // We need to handle this manually to ensure async tasks complete before the app exits.
        args.Handled = true;

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
                await Services.GetRequiredService<IPresenceManager>().ShutdownAsync();
                await SaveApplicationStateAsync(Services);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during application shutdown.");
        }
        finally
        {
            await Log.CloseAndFlushAsync();

            if (Services is IAsyncDisposable asyncDisposableServices)
                await asyncDisposableServices.DisposeAsync();
            else if (Services is IDisposable disposableServices) 
                disposableServices.Dispose();

            // Force process exit to ensure all threads (like VLC) are terminated.
            Current.Exit();
            Process.GetCurrentProcess().Kill();
        }
    }

    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = ShowCrashReportAndExitAsync(e.Exception);
    }

    private async Task ShowCrashReportAndExitAsync(Exception ex)
    {
        var exceptionDetails = ex.ToString();
        var originalLogPath = _currentLogFilePath ?? "Not set";

        Log.Fatal(ex,
            "A critical error occurred. Application will now terminate. Log path: {LogPath}",
            originalLogPath);

        // Primary strategy: Get logs from the in-memory sink.
        var logContent = MemoryLog.Instance.GetContent();

        // Fallback strategy: If memory is empty, try reading the log file from disk.
        if (string.IsNullOrWhiteSpace(logContent))
            try
            {
                await Task.Delay(250).ConfigureAwait(false);
                logContent = await File.ReadAllTextAsync(originalLogPath).ConfigureAwait(false);
            }
            catch (Exception fileEx)
            {
                logContent =
                    $"Could not retrieve logs from memory. The fallback attempt to read the log file failed.\n" +
                    $"Expected Path: '{originalLogPath}'\n" +
                    $"Error: {fileEx.Message}";
            }

        var fullCrashReport = $"{logContent}\n\n--- EXCEPTION DETAILS ---\n{exceptionDetails}";

        if (MainDispatcherQueue == null)
        {
            await Log.CloseAndFlushAsync();
            Current?.Exit();
            Process.GetCurrentProcess().Kill();
            return;
        }

        var dispatcherService = Services?.GetService<IDispatcherService>();
        if (dispatcherService == null && MainDispatcherQueue != null)
        {
            dispatcherService = new DispatcherService(MainDispatcherQueue);
        }

        if (dispatcherService == null)
        {
            await Log.CloseAndFlushAsync();
            Current?.Exit();
            Process.GetCurrentProcess().Kill();
            return;
        }

        await dispatcherService.EnqueueAsync(async () =>
        {
            try
            {
                var uiService = Services?.GetRequiredService<IUIService>();
                if (uiService != null)
                {
                    var result = await uiService.ShowCrashReportDialogAsync(
                        "Critical Error",
                        "Nagi has encountered a critical error and must close. We are sorry for the inconvenience.",
                        fullCrashReport,
                        "https://github.com/Anthonyy232/Nagi/issues"
                    );

                    if (result == CrashReportResult.Reset)
                    {
                        await ResetApplicationDataAsync();
                        return; // App will restart in ResetApplicationDataAsync
                    }
                }
            }
            catch (Exception dialogEx)
            {
                Debug.WriteLine($"Failed to show the crash report dialog: {dialogEx}");
            }
            finally
            {
                await Log.CloseAndFlushAsync();
                Current?.Exit();
                Process.GetCurrentProcess().Kill();
            }
        });
    }

    private async Task ResetApplicationDataAsync()
    {
        try
        {
            Log.Information("User requested a full application data reset from the crash dialog.");

            // Determine paths first so we have them for manual fallback
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", true, true)
                .Build();
            var pathConfig = new PathConfiguration(configuration);

            if (Services != null)
            {
                try
                {
                    var settingsService = Services.GetRequiredService<ISettingsService>();
                    var libraryService = Services.GetRequiredService<ILibraryService>();

                    // Use robust service methods first
                    await settingsService.ResetToDefaultsAsync();
                    await libraryService.ClearAllLibraryDataAsync();
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
            
            // Just in case RestartWithoutElevation doesn't exit the process immediately
            Current.Exit();
            Process.GetCurrentProcess().Kill();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Critical failure during app reset: {ex}");
            Log.Fatal(ex, "Critical failure during app reset.");
            Current.Exit();
            Process.GetCurrentProcess().Kill();
        }
    }

    private void PerformManualFileReset(IPathConfiguration pathConfig)
    {
        try
        {
            // 1. Packaged apps store settings in LocalSettings, not settings.json
            if (PathConfiguration.IsRunningInPackage())
            {
                try
                {
                    ApplicationData.Current.LocalSettings.Values.Clear();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to clear LocalSettings in packaged mode.");
                }
            }

            // 2. Clear known settings and state files (unpackaged uses these, packaged might have fallbacks)
            SafelyDeleteFile(pathConfig.SettingsFilePath);
            SafelyDeleteFile(pathConfig.PlaybackStateFilePath);
            
            // 3. Delete the database
            SafelyDeleteFile(pathConfig.DatabasePath);

            // 4. Clear cache directories
            SafelyDeleteDir(pathConfig.AlbumArtCachePath);
            SafelyDeleteDir(pathConfig.ArtistImageCachePath);
            SafelyDeleteDir(pathConfig.PlaylistImageCachePath);
            SafelyDeleteDir(pathConfig.LrcCachePath);
            
            Log.Information("Manual file reset completed.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Critical failure during manual file reset.");
        }
    }

    private void SafelyDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete file: {Path}", path);
        }
    }

    private void SafelyDeleteDir(string path)
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
    ///     Shows a warning dialog if the application is running with administrator privileges.
    ///     The FolderPicker API doesn't work properly when running elevated.
    /// </summary>
    private void ShowElevationWarningIfNeededAsync()
    {
        if (!ElevationHelper.IsRunningAsAdministrator()) return;

        // Use the dispatcher to ensure the UI is fully loaded before showing the dialog.
        MainDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            // Wait briefly for XamlRoot to become available after page load.
            const int maxRetries = 10;
            for (var i = 0; i < maxRetries && RootWindow?.Content?.XamlRoot is null; i++)
                await Task.Delay(100);

            if (RootWindow?.Content?.XamlRoot is null)
            {
                _logger?.LogWarning("Could not show elevation warning: XamlRoot is not available.");
                return;
            }

            _logger?.LogWarning("Application is running with administrator privileges. FolderPicker may not work.");

            var dialog = new ContentDialog
            {
                Title = "Running as Administrator",
                Content = "Nagi is currently running as Administrator. This may prevent some features " +
                          "(like adding music folders) from working correctly.\n\n" +
                          "For the best experience, please restart Nagi without administrator privileges.",
                PrimaryButtonText = "Restart Normally",
                CloseButtonText = "Continue Anyway",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootWindow.Content.XamlRoot
            };

            DialogThemeHelper.ApplyThemeOverrides(dialog);
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _logger?.LogInformation("User chose to restart without elevation.");
                ElevationHelper.RestartWithoutElevation();
            }
            else
            {
                _logger?.LogInformation("User chose to continue running as administrator.");
            }
        });
    }

    /// <summary>
    ///     Sets the initial page of the application based on whether a music library has been configured.
    /// </summary>
    public async Task CheckAndNavigateToMainContent()
    {
        if (RootWindow is null || Services is null) return;

        var libraryService = Services.GetRequiredService<ILibraryService>();
        var hasFolders = (await libraryService.GetAllFoldersAsync()).Any();

        // This sequence is critical to prevent a theme flash on startup.
        // 1. Set the content, which temporarily uses the OS theme.
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
            // 2. Fetch the user's saved theme and apply it to the root element.
            var settingsService = Services.GetRequiredService<IUISettingsService>();
            var themeService = Services.GetRequiredService<IThemeService>();
            var savedTheme = await settingsService.GetThemeAsync();
            themeService.ApplyTheme(savedTheme);

            // 3. Notify the MainWindow that content is loaded and themed, so it can
            //    update its custom title bar and backdrop to match.
            mainWindow.NotifyContentLoaded();
            mainWindow.InitializeCustomTitleBar();
        }
    }

    internal void ApplyThemeInternal(ElementTheme themeToApply)
    {
        if (RootWindow?.Content is not FrameworkElement rootElement) return;

        rootElement.RequestedTheme = themeToApply;
        Services?.GetRequiredService<IThemeService>().ReapplyCurrentDynamicThemeAsync();

        if (RootWindow is MainWindow mainWindow) mainWindow.InitializeCustomTitleBar();
    }

    public void SetAppPrimaryColorBrushColor(Color newColor)
    {
        if (Resources.TryGetValue("AppPrimaryColorBrush", out var brushObject) &&
            brushObject is SolidColorBrush appPrimaryColorBrush)
        {
            if (appPrimaryColorBrush.Color != newColor)
            {
                appPrimaryColorBrush.Color = newColor;

                // Refresh taskbar icons to match the new primary color.
                // The service handles debouncing internally.
                _ = (RootWindow as MainWindow)?.TaskbarService?.RefreshIconsAsync();
            }
        }
        else
        {
            _logger?.LogCritical("AppPrimaryColorBrush resource not found");
        }
    }

    public bool TryParseHexColor(string hex, out Color color)
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
        if (_window is null || Services is null) return;

        var settingsService = Services.GetRequiredService<IUISettingsService>();
        var startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
        // Note: We intentionally await the mini-player setting inline in the condition below
        // to keep the startup path simple and explicit about when the main window should
        // be hidden from task switchers like Alt+Tab.

        // Handle the special case where the app should start directly in compact/mini-player view.
        // This avoids minimizing the main window before it's activated, which can cause instability.
        if ((isStartupLaunch || startMinimized) && await settingsService.GetMinimizeToMiniPlayerEnabledAsync())
        {
            var windowService = Services.GetRequiredService<IWindowService>();
            windowService.ShowMiniPlayer();
            // Explicitly hide the main window from the task switcher (e.g., Alt+Tab).
            if (_window?.AppWindow is not null) _window.AppWindow.IsShownInSwitchers = false;
        }
        else if (isStartupLaunch || startMinimized)
        {
            var hideToTray = await settingsService.GetHideToTrayEnabledAsync();
            if (!hideToTray) WindowActivator.ShowMinimized(_window);
        }
        else
        {
            // Default behavior: activate and show the main window.
            // Use ShowAndActivate to reliably bring the window to the foreground,
            // bypassing Windows' focus-stealing prevention.
            var win32Service = Services.GetRequiredService<IWin32InteropService>();
            WindowActivator.ShowAndActivate(_window, win32Service);
        }
    }

    private void EnqueuePostLaunchTasks()
    {
        MainDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                if (Services is null) return;

                var audioPlayerService = Services.GetRequiredService<IAudioPlayer>();
                audioPlayerService.InitializeSmtc();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize System Media Transport Controls");
            }
        });
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (Services is null) return;
        try
        {
            #if !MSIX_PACKAGE
                var updateService = Services.GetRequiredService<IUpdateService>();
                await updateService.CheckForUpdatesOnStartupAsync();
            #else
                await Task.CompletedTask;
            #endif
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed during startup update check");
        }
    }
}