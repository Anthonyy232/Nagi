using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.UI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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

    public App()
    {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        CoreApplication.Suspending += OnSuspending;
        LibVLCSharp.Core.Initialize();
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

        var sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _currentLogFilePath = Path.Combine(tempPathConfig.LogsDirectory, $"log-{sessionId}.txt");

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.File(_currentLogFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
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

            HandleInitialActivation(args.UWPLaunchActivatedEventArgs);

            var restoreSession = _fileActivationQueue.IsEmpty;
            await InitializeCoreServicesAsync(restoreSession);

            await CheckAndNavigateToMainContent();

            ProcessFileActivationQueue();

            var isStartupLaunch = Environment.GetCommandLineArgs()
                .Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            await HandleWindowActivationAsync(isStartupLaunch);

            PerformPostLaunchTasks();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly during startup.");
            await Log.CloseAndFlushAsync();
            throw;
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
                var shouldActivateWindow = string.IsNullOrEmpty(filePath);
                if (shouldActivateWindow)
                {
                    _window.AppWindow.Show();
                    _window.Activate();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "HandleExternalActivation: Exception during window activation");
                if (string.IsNullOrEmpty(filePath))
                {
                    _window.AppWindow.Show();
                    _window.Activate();
                }
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
            await Services.GetRequiredService<IWindowService>().InitializeAsync();
            await Services.GetRequiredService<IMusicPlaybackService>().InitializeAsync(restoreSession);
            await Services.GetRequiredService<IPresenceManager>().InitializeAsync();
            await Services.GetRequiredService<TrayIconViewModel>().InitializeAsync();

            var offlineScrobbleService = Services.GetRequiredService<IOfflineScrobbleService>();
            offlineScrobbleService.Start();
            await offlineScrobbleService.ProcessQueueAsync();
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

        ConfigureAppSettingsServices(services);
        ConfigureCoreLogicServices(services);
        ConfigureWinUIServices(services, window, dispatcherQueue, appInstance);
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
        services.AddSingleton<IMetadataService, TagLibMetadataService>();
        services.AddSingleton<ILrcService, LrcService>();

        services.AddSingleton<IApiKeyService, ApiKeyService>();
        services.AddSingleton<ILastFmMetadataService, LastFmMetadataService>();
        services.AddSingleton<ILastFmAuthService, LastFmAuthService>();
        services.AddSingleton<ISpotifyService, SpotifyService>();
        services.AddSingleton<ILastFmScrobblerService, LastFmScrobblerService>();
        services.AddSingleton<IPresenceManager, PresenceManager>();
        services.AddSingleton<IPresenceService, DiscordPresenceService>();
        services.AddSingleton<IPresenceService, LastFmPresenceService>();
        services.AddSingleton<IUpdateService, VelopackUpdateService>();

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
        services.AddSingleton<ITrayPopupService, TrayPopupService>();
        services.AddSingleton<IAudioPlayer>(provider =>
            new LibVlcAudioPlayerService(provider.GetRequiredService<IDispatcherService>(),
                provider.GetRequiredService<ILogger<LibVlcAudioPlayerService>>()));
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

    private static void InitializeDatabase(IServiceProvider services)
    {
        try
        {
            var dbContextFactory = services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            using var dbContext = dbContextFactory.CreateDbContext();
            dbContext.Database.Migrate();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to initialize or migrate database.");
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

            InitializeDatabase(Services);
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
        if (Services is not null) await SaveApplicationStateAsync(Services);

        deferral.Complete();
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
            _logger?.LogError(ex, "Failed to save or clear playback state");
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (Services is not null)
            try
            {
                _logger?.LogInformation("Window is closing. Shutting down services.");
                await Services.GetRequiredService<IPresenceManager>().ShutdownAsync();
                await SaveApplicationStateAsync(Services);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during service shutdown.");
            }
            finally
            {
                await Log.CloseAndFlushAsync();

                if (Services is IAsyncDisposable asyncDisposableServices)
                    await asyncDisposableServices.DisposeAsync();
                else if (Services is IDisposable disposableServices) disposableServices.Dispose();
            }
        else
            await Log.CloseAndFlushAsync();

        Current?.Exit();
    }

    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var exceptionDetails = e.Exception.ToString();
        var originalLogPath = _currentLogFilePath ?? "Not set";

        Log.Fatal(e.Exception,
            "An unhandled exception occurred. Application will now terminate. Log path at time of crash: {LogPath}",
            originalLogPath);
        Log.CloseAndFlush();

        // Primary strategy: Get logs from the in-memory sink.
        var logContent = MemoryLog.Instance.GetContent();

        // Fallback strategy: If memory is empty, try reading the log file from disk.
        if (string.IsNullOrWhiteSpace(logContent))
            try
            {
                Thread.Sleep(250);
                logContent = File.ReadAllText(originalLogPath);
            }
            catch (Exception fileEx)
            {
                logContent =
                    $"Could not retrieve logs from memory. The fallback attempt to read the log file failed.\n" +
                    $"Expected Path: '{originalLogPath}'\n" +
                    $"Error: {fileEx.Message}";
            }

        var fullCrashReport = $"{logContent}\n\n--- UNHANDLED EXCEPTION DETAILS ---\n{exceptionDetails}";

        MainDispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                var uiService = Services?.GetRequiredService<IUIService>();
                if (uiService != null)
                    await uiService.ShowCrashReportDialogAsync(
                        "Application Error",
                        "Nagi has encountered a critical error and must close. We are sorry for the inconvenience.",
                        fullCrashReport,
                        "https://github.com/Anthonyy232/Nagi/issues"
                    );
            }
            catch (Exception dialogEx)
            {
                Debug.WriteLine($"Failed to show the crash report dialog: {dialogEx}");
            }
            finally
            {
                Current.Exit();
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
            await Services.GetRequiredService<LibraryViewModel>().InitializeAsync();
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
        Services?.GetRequiredService<IThemeService>().ReapplyCurrentDynamicTheme();

        if (RootWindow is MainWindow mainWindow) mainWindow.InitializeCustomTitleBar();
    }

    public void SetAppPrimaryColorBrushColor(Color newColor)
    {
        if (Resources.TryGetValue("AppPrimaryColorBrush", out var brushObject) &&
            brushObject is SolidColorBrush appPrimaryColorBrush)
        {
            if (appPrimaryColorBrush.Color != newColor) appPrimaryColorBrush.Color = newColor;
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
            _window.Activate();
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
#endif
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed during startup update check");
        }
    }
}