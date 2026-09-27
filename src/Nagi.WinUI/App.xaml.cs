using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
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
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Services.Implementations.Presence;
using Nagi.Core.Services.Implementations.Romanization;
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

    /// <summary>
    ///     Gets Nagi's default brand accent color, derived from the app logo.
    /// </summary>
    public static Color DefaultAccentColor { get; } = Color.FromArgb(255, 0x21, 0x94, 0x4B);

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
        services.AddSingleton<IPathConfiguration, PathConfiguration>();
        services.ConfigureHttpClientDefaults(builder => builder.ConfigureHttpClient(client =>
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Nagi/1.0 (+https://github.com/Anthonyy232/Nagi)")));
        services.AddHttpClient("")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
            });

        // Image downloads are larger payloads and tolerate slower CDNs; use a longer timeout.
        services.AddHttpClient("ImageDownloader")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

        ConfigureAppSettingsServices(services);
        ConfigureCoreLogicServices(services);
        ConfigureWinUIServices(services, dispatcherQueue, appInstance);
        ConfigureViewModels(services);

        return services.BuildServiceProvider();
    }

    private static void ConfigureAppSettingsServices(IServiceCollection services)
    {
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
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = pathConfig.DatabasePath }.ToString());
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

        services.AddProviderPipelines(
        [
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.MusicBrainz,
                // MusicBrainz mandates 1 req/s per IP with a UA identifying the client.
                // Excess traffic returns 503 until the rate falls back under 1 RPS.
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 1,
                    MaxConcurrent = 1,
                    BaseRetryDelay = TimeSpan.FromSeconds(1),
                },
            },

            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.TheAudioDb,
                // Free tier: 30 req/min. Once breached, the key is locked out for a full
                // minute — short retry delays just waste calls. Wait the full minute, once.
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 1,
                    Window = TimeSpan.FromSeconds(2),
                    MaxConcurrent = 2,
                    MaxRetries = 1,
                    BaseRetryDelay = TimeSpan.FromSeconds(60),
                    MaxRetryDelay = TimeSpan.FromSeconds(75),
                },
            },

            // Last.fm: metadata, auth, and scrobbling all run through a single provider so an
            // auth-trip on one of them pauses the others (shared breaker). Last.fm's limits
            // are undisclosed/dynamic; 5 RPS/IP is the long-standing convention.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.LastFm,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 5,
                    MaxConcurrent = 4,
                    BaseRetryDelay = TimeSpan.FromSeconds(5),
                },
            },

            // Fanart.tv: effectively unlimited for normal use (token bucket, 429 rare). The
            // pipeline auto-honors Retry-After if the service ever pushes back.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.FanartTv,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 10,
                    MaxConcurrent = 8,
                },
            },

            // LRCLIB: no rate limit documented. Keep concurrency reasonable to be polite.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.LrcLib,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 20,
                    MaxConcurrent = 8,
                },
            },

            // NetEase: unofficial API; treat aggressively-rate-limited and back off fast.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.NetEase,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 2,
                    MaxConcurrent = 2,
                    BaseRetryDelay = TimeSpan.FromSeconds(3),
                },
            },

            // ListenBrainz: dynamic limit advertised via X-RateLimit-* headers. The pipeline
            // honors X-RateLimit-Reset-In on 429 (alongside Retry-After), so we can run at a
            // healthy steady-state and let the server tell us when to back off.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.ListenBrainz,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 5,
                    MaxConcurrent = 4,
                },
            },

            // Nagi's own API server (key bootstrap). Internal infra; conservative retry.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.NagiApi,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 5,
                    MaxConcurrent = 4,
                },
            },

            // Generic CDN image downloads. Provider id is shared by all artist/album image
            // fetches because the source CDN isn't always knowable from the URL.
            new ProviderPolicy
            {
                ProviderId = ServiceProviderIds.ImageDownload,
                Channel = new ChannelPolicy
                {
                    PermitsPerWindow = 10,
                    MaxConcurrent = 4,
                },
            },
        ]);

        services.AddSingleton<ILastFmMetadataService, LastFmMetadataService>();
        services.AddSingleton<ILastFmAuthService, LastFmAuthService>();
        services.AddSingleton<IMusicBrainzService, MusicBrainzService>();
        services.AddSingleton<IFanartTvService, FanartTvService>();
        services.AddSingleton<ITheAudioDbService, TheAudioDbService>();
        services.AddSingleton<INetEaseLyricsService, NetEaseLyricsService>();
        services.AddSingleton<LastFmScrobblerService>();
        services.AddSingleton<ILastFmScrobblerService>(sp => sp.GetRequiredService<LastFmScrobblerService>());
        services.AddSingleton<IListenSubmitter>(sp => sp.GetRequiredService<LastFmScrobblerService>());
        services.AddSingleton<ListenBrainzScrobblerService>();
        services.AddSingleton<IListenBrainzScrobblerService>(sp => sp.GetRequiredService<ListenBrainzScrobblerService>());
        services.AddSingleton<IListenSubmitter>(sp => sp.GetRequiredService<ListenBrainzScrobblerService>());
        services.AddSingleton<IPresenceManager, PresenceManager>();
        services.AddSingleton<IPresenceService, DiscordPresenceService>();
        services.AddSingleton<IPresenceService, LastFmPresenceService>();
        services.AddSingleton<IPresenceService, ListenBrainzPresenceService>();
        services.AddSingleton<ISmartPlaylistService, SmartPlaylistService>();
        services.AddSingleton<IOnlineLyricsService, LrcLibService>();
        services.AddSingleton<IRomanizationProvider, JapaneseRomanizationProvider>();
        services.AddSingleton<IRomanizationProvider, DevanagariRomanizationProvider>();
        services.AddSingleton<IRomanizationProvider, KoreanRomanizationProvider>();
        services.AddSingleton<IRomanizationCatalogVerifier, EcdsaRomanizationCatalogVerifier>();
        services.AddSingleton<IRomanizationPackManager, RomanizationPackManager>();
        services.AddSingleton<ILyricRomanizationService, LyricRomanizationService>();
        services.AddSingleton<IPlaylistExportService, M3uPlaylistExportService>();
        services.AddSingleton<IBackupRestoreService, BackupRestoreService>();
        services.AddSingleton<IStatisticsService, StatisticsService>();

        // FFmpeg and ReplayGain services
        services.AddSingleton<IFFmpegService, FFmpegService>();
        services.AddSingleton<ILoudnessAnalyzer, FFmpegLoudnessAnalyzer>();
        services.AddSingleton<IReplayGainService, ReplayGainService>();

    }

    private static void ConfigureWinUIServices(IServiceCollection services,
        DispatcherQueue dispatcherQueue, App appInstance)
    {
        services.AddSingleton<IWin32InteropService, Win32InteropService>();
        services.AddSingleton<IWindowService, WindowService>();
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
        services.AddSingleton<IViewModelFactory, ViewModelFactory>();
        services.AddSingleton<IAudioPlayer, LibVlcAudioPlayerService>();
        services.AddTransient<ITaskbarService, TaskbarService>();
    }

    private static void ConfigureViewModels(IServiceCollection services)
    {
        // Global singletons - persist for app lifetime
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<TrayIconViewModel>();

        // List/Grid ViewModels - Singleton for fast navigation, refresh on data changes
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<PlaylistViewModel>();
        services.AddSingleton<FolderViewModel>();
        services.AddSingleton<ArtistViewModel>();
        services.AddSingleton<AlbumViewModel>();
        services.AddSingleton<GenreViewModel>();
        services.AddSingleton<InsightsViewModel>();

        // Detail/Context ViewModels
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<OnboardingViewModel>();
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
            // 1. Packaged apps store settings in LocalSettings, not settings.json
            try
            {
                ApplicationData.Current.LocalSettings.Values.Clear();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to clear LocalSettings.");
            }

            // 2. Clear known settings and state files
            SafelyDeleteFile(pathConfig.SettingsFilePath);
            SafelyDeleteFile(pathConfig.PlaybackStateFilePath);

            // 3. Delete the database
            DeleteDatabaseFiles(pathConfig.DatabasePath);

            // 4. Clear cache directories
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

        var libraryService = Services.GetRequiredService<ILibraryService>();
        var hasFolders = await libraryService.HasAnyFolderAsync();
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
