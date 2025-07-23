using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Data;
using Nagi.Helpers;
using Nagi.Pages;
using Nagi.Services;
using Nagi.Services.Abstractions;
using Nagi.Services.Implementations;
using Nagi.Services.Implementations.WinUI;
using Nagi.Services.Presence;
using Nagi.ViewModels;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.UI;
using WinRT;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

#if !MSIX_PACKAGE
using Velopack;
using Velopack.Sources;
#endif

namespace Nagi;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// This class serves as the main entry point and central hub for the application.
/// </summary>
public partial class App : Application {
    private static Color? _systemAccentColor;
    private Window? _window;
    private MicaController? _micaController;
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

    public App() {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        CoreApplication.Suspending += OnSuspending;

#if !MSIX_PACKAGE
        // Initializes and runs the Velopack update manager.
        VelopackApp.Build().Run();
#endif
    }

    /// <summary>
    /// Gets the current instance of the application.
    /// </summary>
    public static App? CurrentApp { get; private set; }

    /// <summary>
    /// Gets the main application window.
    /// </summary>
    public static Window? RootWindow => CurrentApp?._window;

    /// <summary>
    /// Gets the configured service provider for dependency injection.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    /// <summary>
    /// Gets the dispatcher queue for the main UI thread.
    /// </summary>
    public static DispatcherQueue? MainDispatcherQueue => CurrentApp?._window?.DispatcherQueue;

    /// <summary>
    /// Gets the system's accent color, with a fallback to a default color.
    /// The color is cached after the first access.
    /// </summary>
    public static Color SystemAccentColor {
        get {
            if (_systemAccentColor is null) {
                if (Current.Resources.TryGetValue("SystemAccentColor", out object? value) && value is Color color) {
                    _systemAccentColor = color;
                }
                else {
                    _systemAccentColor = Colors.SlateGray;
                }
            }
            return _systemAccentColor.Value;
        }
    }

    /// <summary>
    /// Configures and builds the service provider for dependency injection.
    /// </summary>
    private static IServiceProvider ConfigureServices(Window window, DispatcherQueue dispatcherQueue, App appInstance) {
        var services = new ServiceCollection();
        ConfigureCoreServices(services);
        ConfigureUIServices(services, window, dispatcherQueue, appInstance);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Registers core, non-UI services with the dependency injection container.
    /// </summary>
    public static void ConfigureCoreServices(IServiceCollection services) {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddSingleton<PathConfiguration>();
        services.AddHttpClient();

#if !MSIX_PACKAGE
        services.AddSingleton(provider => {
            var source = new GithubSource("https://github.com/Anthonyy232/Nagi", null, false);
            return new UpdateManager(source);
        });
#endif

        services.AddDbContextFactory<MusicDbContext>((serviceProvider, options) => {
            var pathConfig = serviceProvider.GetRequiredService<PathConfiguration>();
            options.UseSqlite($"Data Source={pathConfig.DatabasePath}");
        });

        // File and Data Services
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IMetadataExtractor, TagLibMetadataExtractor>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IUpdateService, VelopackUpdateService>();

        // Library Service (registered for multiple interfaces)
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryReader>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryWriter>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryScanner>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<IPlaylistService>(provider => provider.GetRequiredService<LibraryService>());

        // External API and Credential Services
        services.AddSingleton<IApiKeyService, ApiKeyService>();
        services.AddSingleton<ILastFmMetadataService, LastFmMetadataService>();
        services.AddSingleton<ILastFmAuthService, LastFmAuthService>();
        services.AddSingleton<ISpotifyService, SpotifyService>();
        services.AddSingleton<ICredentialLockerService, CredentialLockerService>();
        services.AddSingleton<ILastFmScrobblerService, LastFmScrobblerService>();
        services.AddSingleton<IOfflineScrobbleService, OfflineScrobbleService>();

        // Presence Services
        services.AddSingleton<IPresenceManager, PresenceManager>();
        services.AddSingleton<IPresenceService, DiscordPresenceService>();
        services.AddSingleton<IPresenceService, LastFmPresenceService>();
    }

    /// <summary>
    /// Registers UI-specific services and ViewModels with the dependency injection container.
    /// </summary>
    private static void ConfigureUIServices(IServiceCollection services, Window window, DispatcherQueue dispatcherQueue, App appInstance) {
        // UI Abstraction Services
        services.AddSingleton<IWindowService>(sp => new WinUIWindowService(window, sp.GetRequiredService<IWin32InteropService>()));
        services.AddSingleton<IUIService>(sp => new WinUIUIService(window));
        services.AddSingleton<IDispatcherService>(sp => new WinUIDispatcherService(dispatcherQueue));
        services.AddSingleton<IThemeService>(sp => new WinUIThemeService(appInstance, sp));
        services.AddSingleton<IApplicationLifecycle>(sp => new WinUIApplicationLifecycle(appInstance, sp));
        services.AddSingleton<IAppInfoService, WinUIAppInfoService>();
        services.AddSingleton<IWin32InteropService, Win32InteropService>();
        services.AddSingleton<INavigationService, NavigationService>();

        // Playback and UI Control Services
        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<ITrayPopupService, TrayPopupService>();
        services.AddSingleton<IAudioPlayer>(provider => new AudioPlayerService(dispatcherQueue));

        // ViewModels
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
    }

    /// <summary>
    /// Ensures the database is created and all pending migrations are applied.
    /// </summary>
    private static void InitializeDatabase(IServiceProvider services) {
        try {
            var dbContextFactory = services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            using var dbContext = dbContextFactory.CreateDbContext();
            dbContext.Database.Migrate();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[CRITICAL] App: Failed to initialize or migrate database. {ex.Message}");
        }
    }

    /// <summary>
    /// Called when the application is launched. This is the main entry point for application logic.
    /// </summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args) {
        bool isStartupLaunch = Environment.GetCommandLineArgs()
            .Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        InitializeWindowAndServices();
        InitializeSystemIntegration();
        await InitializeCoreServicesAsync();

        await CheckAndNavigateToMainContent();
        await HandleWindowActivationAsync(isStartupLaunch);

        PerformPostLaunchTasks();
    }

    /// <summary>
    /// Creates the main window, configures services, and initializes the database.
    /// </summary>
    private void InitializeWindowAndServices() {
        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        Services = ConfigureServices(_window, _window.DispatcherQueue, this);
        InitializeDatabase(Services);
    }

    /// <summary>
    /// Sets up window icon, backdrop, and theme.
    /// </summary>
    private void InitializeSystemIntegration() {
        try {
            var interopService = Services!.GetRequiredService<IWin32InteropService>();
            interopService.SetWindowIcon(_window!, "Assets/AppLogo.ico");
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] App: Failed to set window icon. {ex.Message}");
        }

        TrySetMicaBackdrop();
        Services!.GetRequiredService<IThemeService>().ReapplyCurrentDynamicTheme();
    }

    /// <summary>
    /// Initializes essential background services asynchronously.
    /// </summary>
    private async Task InitializeCoreServicesAsync() {
        if (Services is null) return;
        try {
            await Services.GetRequiredService<IMusicPlaybackService>().InitializeAsync();
            await Services.GetRequiredService<IPresenceManager>().InitializeAsync();
            await Services.GetRequiredService<TrayIconViewModel>().InitializeAsync();

            var offlineScrobbleService = Services.GetRequiredService<IOfflineScrobbleService>();
            offlineScrobbleService.Start();
            await offlineScrobbleService.ProcessQueueAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] App: Failed to initialize services. {ex.Message}");
        }
    }

    /// <summary>
    /// Kicks off tasks that can run after the main window is visible.
    /// </summary>
    private void PerformPostLaunchTasks() {
        _ = CheckForUpdatesOnStartupAsync();
        EnqueuePostLaunchTasks();
    }

    /// <summary>
    /// Handles the application suspending event, saving state before termination.
    /// </summary>
    private async void OnSuspending(object? sender, SuspendingEventArgs e) {
        var deferral = e.SuspendingOperation.GetDeferral();
        if (Services is not null) {
            await SaveApplicationStateAsync(Services);
        }
        deferral.Complete();
    }

    /// <summary>
    /// Saves the current playback state if the setting is enabled.
    /// </summary>
    private async Task SaveApplicationStateAsync(IServiceProvider services) {
        var settingsService = services.GetRequiredService<ISettingsService>();
        var musicPlaybackService = services.GetRequiredService<IMusicPlaybackService>();

        try {
            if (await settingsService.GetRestorePlaybackStateEnabledAsync()) {
                await musicPlaybackService.SavePlaybackStateAsync();
            }
            else {
                await settingsService.ClearPlaybackStateAsync();
            }
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] App: Failed to save or clear playback state. {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the main window closing event, ensuring graceful shutdown of services.
    /// </summary>
    private async void OnWindowClosed(object sender, WindowEventArgs args) {
        if (Services is not null) {
            try {
                await Services.GetRequiredService<IPresenceManager>().ShutdownAsync();
                await SaveApplicationStateAsync(Services);
            }
            finally {
                if (Services is IAsyncDisposable asyncDisposableServices) {
                    await asyncDisposableServices.DisposeAsync();
                }
                else if (Services is IDisposable disposableServices) {
                    disposableServices.Dispose();
                }
            }
        }

        _micaController?.Dispose();
        _micaController = null;

        _wsdqHelper?.Dispose();
        _wsdqHelper = null;
    }

    /// <summary>
    /// Global exception handler for any unhandled exceptions on the UI thread.
    /// </summary>
    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        Debug.WriteLine($"[FATAL] App: UNHANDLED EXCEPTION: {e.Exception}");
        e.Handled = true;
    }

    /// <summary>
    /// Checks if the library is configured and navigates to the appropriate initial page.
    /// </summary>
    public async Task CheckAndNavigateToMainContent() {
        if (RootWindow is null || Services is null) return;

        var libraryService = Services.GetRequiredService<ILibraryService>();
        bool hasFolders = (await libraryService.GetAllFoldersAsync()).Any();

        if (hasFolders) {
            if (RootWindow.Content is not MainPage) {
                RootWindow.Content = new MainPage();
            }
            await Services.GetRequiredService<LibraryViewModel>().InitializeAsync();
        }
        else {
            if (RootWindow.Content is not OnboardingPage) {
                RootWindow.Content = new OnboardingPage();
            }
        }

        if (RootWindow.Content is FrameworkElement currentContent && RootWindow is MainWindow mainWindow) {
            var settingsService = Services.GetRequiredService<ISettingsService>();
            currentContent.RequestedTheme = await settingsService.GetThemeAsync();
            mainWindow.InitializeCustomTitleBar();
        }
    }

    /// <summary>
    /// Applies the specified theme to the root element of the application.
    /// </summary>
    internal void ApplyThemeInternal(ElementTheme themeToApply) {
        if (RootWindow?.Content is not FrameworkElement rootElement) return;

        rootElement.RequestedTheme = themeToApply;
        Services?.GetRequiredService<IThemeService>().ReapplyCurrentDynamicTheme();

        if (RootWindow is MainWindow mainWindow) {
            mainWindow.InitializeCustomTitleBar();
        }
    }

    /// <summary>
    /// Sets the color of the application's primary color brush resource.
    /// </summary>
    public void SetAppPrimaryColorBrushColor(Color newColor) {
        if (Resources.TryGetValue("AppPrimaryColorBrush", out object? brushObject) &&
            brushObject is SolidColorBrush appPrimaryColorBrush) {
            if (appPrimaryColorBrush.Color != newColor) {
                appPrimaryColorBrush.Color = newColor;
            }
        }
        else {
            Debug.WriteLine("[CRITICAL] App: AppPrimaryColorBrush resource not found.");
        }
    }

    /// <summary>
    /// Attempts to parse a hexadecimal string into a Color object.
    /// </summary>
    /// <param name="hex">The hex string (e.g., "#RRGGBB" or "#AARRGGBB").</param>
    /// <param name="color">The output color if parsing is successful.</param>
    /// <returns>True if parsing was successful, otherwise false.</returns>
    public bool TryParseHexColor(string hex, out Color color) {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb)) return false;

        switch (hex.Length) {
            case 6: // RRGGBB
                color = Color.FromArgb(255, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            case 8: // AARRGGBB
                color = Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Determines how the window should be shown on startup (activated, minimized, or hidden).
    /// </summary>
    private async Task HandleWindowActivationAsync(bool isStartupLaunch = false) {
        if (_window is null || Services is null) return;

        var settingsService = Services.GetRequiredService<ISettingsService>();
        bool startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
        bool hideToTray = await settingsService.GetHideToTrayEnabledAsync();

        if (isStartupLaunch || startMinimized) {
            if (!hideToTray) {
                WindowActivator.ShowMinimized(_window);
            }
        }
        else {
            _window.Activate();
        }
    }

    /// <summary>
    /// Enqueues tasks to be run on the UI thread after the application has launched.
    /// </summary>
    private void EnqueuePostLaunchTasks() {
        MainDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Normal, () => {
            if (Services is null) return;
            try {
                var audioPlayerService = Services.GetRequiredService<IAudioPlayer>();
                audioPlayerService.InitializeSmtc();
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ERROR] App: Failed to initialize System Media Transport Controls. {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Applies the Mica backdrop to the main window if supported by the system.
    /// </summary>
    private bool TrySetMicaBackdrop() {
        if (!MicaController.IsSupported()) return false;

        _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
        _wsdqHelper.EnsureDispatcherQueue();

        var configurationSource = new SystemBackdropConfiguration { IsInputActive = true };

        if (RootWindow?.Content is FrameworkElement rootElement) {
            void UpdateTheme() {
                configurationSource.Theme = rootElement.ActualTheme switch {
                    ElementTheme.Dark => SystemBackdropTheme.Dark,
                    ElementTheme.Light => SystemBackdropTheme.Light,
                    _ => SystemBackdropTheme.Default
                };
            }
            rootElement.ActualThemeChanged += (s, e) => UpdateTheme();
            UpdateTheme();
        }

        _micaController = new MicaController();
        _micaController.SetSystemBackdropConfiguration(configurationSource);

        if (RootWindow is not null) {
            _micaController.AddSystemBackdropTarget(RootWindow.As<ICompositionSupportsSystemBackdrop>());
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks for application updates if the corresponding setting is enabled.
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync() {
        if (Services is null) return;
        var updateService = Services.GetRequiredService<IUpdateService>();
        await updateService.CheckForUpdatesOnStartupAsync();
    }
}