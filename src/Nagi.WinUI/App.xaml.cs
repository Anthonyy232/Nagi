using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.UI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
using WinRT;
using LaunchActivatedEventArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

#if !MSIX_PACKAGE
using Velopack;
using Velopack.Sources;
#endif

namespace Nagi.WinUI;

/// <summary>
///     Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application {
    private static Color? _systemAccentColor;

    private readonly ConcurrentQueue<string> _fileActivationQueue = new();
    private volatile bool _isProcessingFileQueue;
    private Window? _window;

    public App() {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        CoreApplication.Suspending += OnSuspending;
        LibVLCSharp.Core.Initialize();
    }

    public static App? CurrentApp { get; private set; }
    public static Window? RootWindow => CurrentApp?._window;
    public static IServiceProvider? Services { get; private set; }
    public static DispatcherQueue? MainDispatcherQueue => CurrentApp?._window?.DispatcherQueue;

    public static Color SystemAccentColor {
        get {
            _systemAccentColor ??= Current.Resources.TryGetValue("SystemAccentColor", out var value) &&
                                   value is Color color
                ? color
                : Colors.SlateGray;
            return _systemAccentColor.Value;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args) {
        InitializeWindowAndServices();
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

    private void HandleInitialActivation(IActivatedEventArgs args) {
        string? filePath = null;

        if (args.Kind == ActivationKind.File) {
            var fileArgs = args.As<IFileActivatedEventArgs>();
            if (fileArgs.Files.Any()) filePath = fileArgs.Files[0].Path;
        }
        else if (args.Kind == ActivationKind.Launch) {
            var commandLineArgs = Environment.GetCommandLineArgs();
            if (commandLineArgs.Length > 1) filePath = commandLineArgs[1];
        }

        if (!string.IsNullOrEmpty(filePath)) _fileActivationQueue.Enqueue(filePath);
    }

    /// <summary>
    ///     Queues a file path for playback. This can be called from external sources
    ///     (e.g., a single-instance redirection) to open files in the running application.
    /// </summary>
    public void EnqueueFileActivation(string filePath) {
        if (string.IsNullOrEmpty(filePath)) return;
        _fileActivationQueue.Enqueue(filePath);
        ProcessFileActivationQueue();
    }

    /// <summary>
    ///     Processes the queue of file activation requests on the main UI thread.
    ///     A flag ensures that the queue is processed by only one thread at a time.
    /// </summary>
    private void ProcessFileActivationQueue() {
        if (_isProcessingFileQueue) return;

        MainDispatcherQueue?.TryEnqueue(async () => {
            if (_isProcessingFileQueue) return;

            _isProcessingFileQueue = true;
            try {
                while (_fileActivationQueue.TryDequeue(out var filePath)) await ProcessFileActivationAsync(filePath);
            }
            catch (Exception ex) {
                Debug.WriteLine(
                    $"[ERROR] App.ProcessFileActivationQueue: Exception while processing queue: {ex.Message}");
            }
            finally {
                _isProcessingFileQueue = false;
            }
        });
    }

    public async Task ProcessFileActivationAsync(string filePath) {
        if (Services is null || string.IsNullOrEmpty(filePath)) {
            Debug.WriteLine("[ERROR] App.ProcessFileActivationAsync: Aborted due to null services or file path.");
            return;
        }

        try {
            var playbackService = Services.GetRequiredService<IMusicPlaybackService>();

            _window?.Activate();
            await playbackService.PlayTransientFileAsync(filePath);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] App.ProcessFileActivationAsync: Failed to process file '{filePath}'. Exception: {ex}");
        }
    }

    private async Task InitializeCoreServicesAsync(bool restoreSession = true) {
        if (Services is null) return;
        try {
            await Services.GetRequiredService<IMusicPlaybackService>().InitializeAsync(restoreSession);
            await Services.GetRequiredService<IPresenceManager>().InitializeAsync();
            await Services.GetRequiredService<TrayIconViewModel>().InitializeAsync();

            var offlineScrobbleService = Services.GetRequiredService<IOfflineScrobbleService>();
            offlineScrobbleService.Start();
            await offlineScrobbleService.ProcessQueueAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] App.InitializeCoreServicesAsync: Failed to initialize services. {ex.Message}");
        }
    }

    /// <summary>
    ///     Configures the dependency injection container for the application.
    /// </summary>
    private static IServiceProvider ConfigureServices(Window window, DispatcherQueue dispatcherQueue, App appInstance) {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", false, true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IPathConfiguration, PathConfiguration>();
        services.AddHttpClient();

        ConfigureAppSettingsServices(services);
        ConfigureCoreLogicServices(services);
        ConfigureWinUIServices(services, window, dispatcherQueue, appInstance);
        ConfigureViewModels(services);

        return services.BuildServiceProvider();
    }

    private static void ConfigureAppSettingsServices(IServiceCollection services) {
        services.AddSingleton<ICredentialLockerService, CredentialLockerService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IUISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
    }

    private static void ConfigureCoreLogicServices(IServiceCollection services) {
        services.AddDbContextFactory<MusicDbContext>((serviceProvider, options) => {
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
            services.AddSingleton(provider => {
                var source = new GithubSource("https://github.com/Anthonyy232/Nagi", null, false);
                return new UpdateManager(source);
            });
#endif
    }

    private static void ConfigureWinUIServices(IServiceCollection services, Window window,
        DispatcherQueue dispatcherQueue, App appInstance) {
        services.AddSingleton<IWin32InteropService, Win32InteropService>();
        services.AddSingleton<IWindowService>(sp => new WindowService(
            window,
            sp.GetRequiredService<IWin32InteropService>(),
            sp.GetRequiredService<IUISettingsService>(),
            sp.GetRequiredService<IDispatcherService>()
        ));
        services.AddSingleton<IUIService>(sp => new UIService(window));
        services.AddSingleton(dispatcherQueue);
        services.AddSingleton<IDispatcherService, DispatcherService>();
        services.AddSingleton<IThemeService>(sp => new ThemeService(appInstance, sp));
        services.AddSingleton<IApplicationLifecycle>(sp => new ApplicationLifecycle(appInstance, sp));
        services.AddSingleton<IAppInfoService, AppInfoService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ITrayPopupService, TrayPopupService>();
        services.AddSingleton<IAudioPlayer>(provider =>
            new LibVlcAudioPlayerService(provider.GetRequiredService<IDispatcherService>()));
    }

    private static void ConfigureViewModels(IServiceCollection services) {
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

    private static void InitializeDatabase(IServiceProvider services) {
        try {
            var dbContextFactory = services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            using var dbContext = dbContextFactory.CreateDbContext();
            dbContext.Database.Migrate();
        }
        catch (Exception ex) {
            Debug.WriteLine(
                $"[CRITICAL] App.InitializeDatabase: Failed to initialize or migrate database. {ex.Message}");
        }
    }

    private void InitializeWindowAndServices() {
        _window = new MainWindow();
        _window.Closed += OnWindowClosed;

        Services = ConfigureServices(_window, _window.DispatcherQueue, this);

        if (_window is MainWindow mainWindow)
            mainWindow.InitializeDependencies(Services.GetRequiredService<IUISettingsService>());

        InitializeDatabase(Services);
    }

    private void InitializeSystemIntegration() {
        try {
            var interopService = Services!.GetRequiredService<IWin32InteropService>();
            interopService.SetWindowIcon(_window!, "Assets/AppLogo.ico");
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] App.InitializeSystemIntegration: Failed to set window icon. {ex.Message}");
        }
    }

    private void PerformPostLaunchTasks() {
        _ = CheckForUpdatesOnStartupAsync();
        EnqueuePostLaunchTasks();
    }

    private async void OnSuspending(object? sender, SuspendingEventArgs e) {
        var deferral = e.SuspendingOperation.GetDeferral();
        if (Services is not null) await SaveApplicationStateAsync(Services);
        deferral.Complete();
    }

    private async Task SaveApplicationStateAsync(IServiceProvider services) {
        var settingsService = services.GetRequiredService<ISettingsService>();
        var musicPlaybackService = services.GetRequiredService<IMusicPlaybackService>();

        try {
            if (await settingsService.GetRestorePlaybackStateEnabledAsync())
                await musicPlaybackService.SavePlaybackStateAsync();
            else
                await settingsService.ClearPlaybackStateAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine(
                $"[ERROR] App.SaveApplicationStateAsync: Failed to save or clear playback state. {ex.Message}");
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args) {
        if (Services is not null)
            try {
                await Services.GetRequiredService<IPresenceManager>().ShutdownAsync();
                await SaveApplicationStateAsync(Services);
            }
            finally {
                // Ensure the service provider and its disposable services are cleaned up.
                if (Services is IAsyncDisposable asyncDisposableServices)
                    await asyncDisposableServices.DisposeAsync();
                else if (Services is IDisposable disposableServices) disposableServices.Dispose();
            }

        Current.Exit();
    }

    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        // Log the unhandled exception to prevent the application from crashing silently.
        Debug.WriteLine($"[FATAL] App.OnAppUnhandledException: {e.Exception}");
        e.Handled = true;
    }

    /// <summary>
    ///     Sets the initial page of the application based on whether a music library has been configured.
    ///     This method follows a specific sequence to prevent theme flashing on startup.
    /// </summary>
    public async Task CheckAndNavigateToMainContent() {
        if (RootWindow is null || Services is null) return;

        var libraryService = Services.GetRequiredService<ILibraryService>();
        var hasFolders = (await libraryService.GetAllFoldersAsync()).Any();

        // The following sequence is critical to prevent theme flashing on startup.

        // 1. Set the content (e.g., MainPage or OnboardingPage). It will temporarily use the OS's theme.
        if (hasFolders) {
            if (RootWindow.Content is not MainPage) RootWindow.Content = new MainPage();
            await Services.GetRequiredService<LibraryViewModel>().InitializeAsync();
        }
        else {
            if (RootWindow.Content is not OnboardingPage) RootWindow.Content = new OnboardingPage();
        }

        if (RootWindow is MainWindow mainWindow) {
            // 2. Fetch the user's saved theme from settings and apply it to the root element.
            var settingsService = Services.GetRequiredService<IUISettingsService>();
            var themeService = Services.GetRequiredService<IThemeService>();
            var savedTheme = await settingsService.GetThemeAsync();
            themeService.ApplyTheme(savedTheme);

            // 3. Notify the MainWindow that the content is now loaded and correctly themed, so it can
            //    update its custom title bar and backdrop to match.
            mainWindow.NotifyContentLoaded();
            mainWindow.InitializeCustomTitleBar();
        }
    }

    internal void ApplyThemeInternal(ElementTheme themeToApply) {
        if (RootWindow?.Content is not FrameworkElement rootElement) return;

        rootElement.RequestedTheme = themeToApply;
        Services?.GetRequiredService<IThemeService>().ReapplyCurrentDynamicTheme();

        if (RootWindow is MainWindow mainWindow) mainWindow.InitializeCustomTitleBar();
    }

    public void SetAppPrimaryColorBrushColor(Color newColor) {
        if (Resources.TryGetValue("AppPrimaryColorBrush", out var brushObject) &&
            brushObject is SolidColorBrush appPrimaryColorBrush) {
            if (appPrimaryColorBrush.Color != newColor) appPrimaryColorBrush.Color = newColor;
        }
        else {
            Debug.WriteLine("[CRITICAL] App.SetAppPrimaryColorBrushColor: AppPrimaryColorBrush resource not found.");
        }
    }

    public bool TryParseHexColor(string hex, out Color color) {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)) return false;

        switch (hex.Length) {
            case 6: // RRGGBB
                color = Color.FromArgb(255, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF),
                    (byte)(argb & 0xFF));
                return true;
            case 8: // AARRGGBB
                color = Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
                    (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            default:
                return false;
        }
    }

    private async Task HandleWindowActivationAsync(bool isStartupLaunch = false) {
        if (_window is null || Services is null) return;

        var settingsService = Services.GetRequiredService<IUISettingsService>();
        var startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
        var hideToTray = await settingsService.GetHideToTrayEnabledAsync();

        if (isStartupLaunch || startMinimized) {
            if (!hideToTray) WindowActivator.ShowMinimized(_window);
        }
        else {
            _window.Activate();
        }
    }

    private void EnqueuePostLaunchTasks() {
        MainDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Normal, () => {
            try {
                if (Services is null) return;

                var audioPlayerService = Services.GetRequiredService<IAudioPlayer>();
                audioPlayerService.InitializeSmtc();
            }
            catch (Exception ex) {
                Debug.WriteLine(
                    $"[ERROR] App.EnqueuePostLaunchTasks: Failed to initialize System Media Transport Controls. {ex.Message}");
            }
        });
    }

    private async Task CheckForUpdatesOnStartupAsync() {
        if (Services is null) return;
        try {
#if !MSIX_PACKAGE
                var updateService = Services.GetRequiredService<IUpdateService>();
                await updateService.CheckForUpdatesOnStartupAsync();
#endif
        }
        catch (Exception ex) {
            Debug.WriteLine(
                $"[ERROR] App.CheckForUpdatesOnStartupAsync: Failed during startup update check. {ex.Message}");
        }
    }
}