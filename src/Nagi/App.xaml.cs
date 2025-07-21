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
using Nagi.ViewModels;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.UI;
using WinRT;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace Nagi;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// This class is the main entry point for the application.
/// </summary>
public partial class App : Application {
    private static Color? _systemAccentColor;
    private Window? _window;
    private MicaController? _micaController;
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App() {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        CoreApplication.Suspending += OnSuspending;

        // Initializes Velopack for application updates. This must be run early in the startup process.
        VelopackApp.Build().Run();
    }

    /// <summary>
    /// Statically initializes the application's services and database.
    /// This runs once before any instance of the App class is created.
    /// </summary>
    static App() {
        // Service configuration is deferred until OnLaunched when the window is available.
    }

    public static App? CurrentApp { get; private set; }
    public static Window? RootWindow => CurrentApp?._window;
    public static IServiceProvider? Services { get; private set; }
    public static DispatcherQueue? MainDispatcherQueue => CurrentApp?._window?.DispatcherQueue;
    public static bool IsExiting { get; set; }

    /// <summary>
    /// Gets the system accent color, with a fallback to a default color.
    /// The color is cached for performance.
    /// </summary>
    public static Color SystemAccentColor {
        get {
            if (_systemAccentColor is null) {
                if (Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color) {
                    _systemAccentColor = color;
                }
                else {
                    // Fallback color if the system resource is unavailable.
                    _systemAccentColor = Colors.SlateGray;
                }
            }
            return _systemAccentColor.Value;
        }
    }

    #region Service Configuration

    /// <summary>
    /// Configures the dependency injection services for the application.
    /// </summary>
    private static IServiceProvider ConfigureServices(Window window, DispatcherQueue dispatcherQueue, App appInstance) {
        var services = new ServiceCollection();

        // Configure services that do not depend on the UI
        ConfigureCoreServices(services);

        // Configure services that depend on the UI
        ConfigureUIServices(services, window, dispatcherQueue, appInstance);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Configures core services that do not have any dependency on the WinUI framework.
    /// This method can be called by design-time tools like EF Core CLI.
    /// </summary>
    public static void ConfigureCoreServices(IServiceCollection services) {
        #region Configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        #endregion

        #region Foundational Services
        services.AddSingleton<PathConfiguration>();
        services.AddHttpClient();
        services.AddSingleton(provider => {
            var source = new GithubSource("https://github.com/Anthonyy232/Nagi", null, false);
            return new UpdateManager(source);
        });
        #endregion

        #region Database
        services.AddDbContextFactory<MusicDbContext>((serviceProvider, options) => {
            var pathConfig = serviceProvider.GetRequiredService<PathConfiguration>();
            options.UseSqlite($"Data Source={pathConfig.DatabasePath}");
        });
        #endregion

        #region Core Application Services
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IMetadataExtractor, TagLibMetadataExtractor>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IWin32InteropService, Win32InteropService>();

        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryReader>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryWriter>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryScanner>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<IPlaylistService>(provider => provider.GetRequiredService<LibraryService>());
        #endregion

        #region External API Services
        services.AddSingleton<IApiKeyService, ApiKeyService>();
        services.AddSingleton<ILastFmService, LastFmService>();
        services.AddSingleton<ISpotifyService, SpotifyService>();
        #endregion
    }

    /// <summary>
    /// Configures services that are dependent on the WinUI framework (Window, DispatcherQueue, etc.).
    /// </summary>
    private static void ConfigureUIServices(IServiceCollection services, Window window, DispatcherQueue dispatcherQueue, App appInstance) {
        #region UI Abstraction Services
        services.AddSingleton<IWindowService>(sp => new WinUIWindowService(window, sp.GetRequiredService<IWin32InteropService>()));
        services.AddSingleton<IUIService>(sp => new WinUIUIService(window));
        services.AddSingleton<IDispatcherService>(sp => new WinUIDispatcherService(dispatcherQueue));
        services.AddSingleton<IThemeService>(sp => new WinUIThemeService(appInstance));
        services.AddSingleton<IApplicationLifecycle>(sp => new WinUIApplicationLifecycle(appInstance, sp));
        services.AddSingleton<IAppInfoService, WinUIAppInfoService>();
        #endregion

        #region UI-Dependent Core Services
        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<ITrayPopupService, TrayPopupService>();
        services.AddSingleton<IAudioPlayer>(provider => {
            if (dispatcherQueue is null)
                throw new InvalidOperationException("DispatcherQueue must be available to create AudioPlayerService.");
            return new AudioPlayerService(dispatcherQueue);
        });
        #endregion

        #region ViewModels
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
        #endregion
    }

    #endregion

    /// <summary>
    /// Ensures the application database is created if it does not already exist.
    /// </summary>
    private static void InitializeDatabase(IServiceProvider services) {
        try {
            var dbContextFactory = services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            using var dbContext = dbContextFactory.CreateDbContext();
            dbContext.RecreateDatabase();
            dbContext.Database.Migrate();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[CRITICAL] App: Failed to initialize or migrate database. {ex.Message}");
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args) {
        bool isStartupLaunch = Environment.GetCommandLineArgs()
            .Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        #region Window and DI Initialization
        _window = new MainWindow();
        _window.Closed += OnWindowClosed;

        // Configure services now that we have a window and dispatcher
        Services = ConfigureServices(_window, _window.DispatcherQueue, this);
        InitializeDatabase(Services);
        #endregion

        #region System Integration
        try {
            var interopService = Services.GetRequiredService<IWin32InteropService>();
            interopService.SetWindowIcon(_window, "Assets/AppLogo.ico");
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] App: Failed to set window icon. {ex.Message}");
        }

        TrySetMicaBackdrop();
        ReapplyCurrentDynamicTheme();
        #endregion

        #region Service Initialization
        try {
            var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
            await playbackService.InitializeAsync();

            var trayIconVm = Services.GetRequiredService<TrayIconViewModel>();
            await trayIconVm.InitializeAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] App: Failed to initialize services. {ex.Message}");
        }
        #endregion

        #region Initial Navigation
        await CheckAndNavigateToMainContent();
        #endregion

        #region Window Activation
        await HandleWindowActivationAsync(isStartupLaunch);
        #endregion

        #region Post-Launch Tasks
        _ = CheckForUpdatesAsync();
        EnqueuePostLaunchTasks();
        #endregion
    }

    /// <summary>
    /// Invoked when application execution is being suspended. Application state should be saved here.
    /// </summary>
    private async void OnSuspending(object? sender, SuspendingEventArgs e) {
        var deferral = e.SuspendingOperation.GetDeferral();
        if (Services is not null) {
            await SaveApplicationStateAsync(Services);
        }
        deferral.Complete();
    }

    /// <summary>
    /// Saves the application's playback state based on user settings.
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
    /// Invoked when the main application window is closed.
    /// </summary>
    private async void OnWindowClosed(object sender, WindowEventArgs args) {
        if (Services is not null) {
            await SaveApplicationStateAsync(Services);

            if (Services.GetService<TrayIconViewModel>() is IDisposable disposableTray) {
                disposableTray.Dispose();
            }
            if (Services.GetService<IWindowService>() is IDisposable disposableWindow) {
                disposableWindow.Dispose();
            }
        }

        _micaController?.Dispose();
        _micaController = null;

        _wsdqHelper?.Dispose();
        _wsdqHelper = null;
    }

    /// <summary>
    /// Handles unhandled exceptions globally to prevent the application from crashing.
    /// </summary>
    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        Debug.WriteLine($"[FATAL] App: UNHANDLED EXCEPTION: {e.Exception}");
        e.Handled = true;
    }

    /// <summary>
    /// Checks if music folders are configured and navigates to the appropriate initial page (Onboarding or Main).
    /// </summary>
    public async Task CheckAndNavigateToMainContent() {
        if (RootWindow is null || Services is null) return;

        var libraryService = Services.GetRequiredService<ILibraryService>();
        bool hasFolders = (await libraryService.GetAllFoldersAsync()).Any();

        if (hasFolders) {
            if (RootWindow.Content is not MainPage) {
                RootWindow.Content = new MainPage();
            }
            var libraryViewModel = Services.GetRequiredService<LibraryViewModel>();
            await libraryViewModel.InitializeAsync();
        }
        else {
            if (RootWindow.Content is not OnboardingPage) {
                RootWindow.Content = new OnboardingPage();
            }
        }

        // Initialize theme and title bar after the main content has been set.
        if (RootWindow.Content is FrameworkElement currentContent && RootWindow is MainWindow mainWindow) {
            var settingsService = Services.GetRequiredService<ISettingsService>();
            currentContent.RequestedTheme = await settingsService.GetThemeAsync();
            mainWindow.InitializeCustomTitleBar();
        }
    }

    #region Theming and Color Management

    /// <summary>
    /// Applies the specified theme to the application's root element.
    /// </summary>
    public void ApplyTheme(ElementTheme themeToApply) {
        if (RootWindow?.Content is not FrameworkElement rootElement) return;

        rootElement.RequestedTheme = themeToApply;
        ReapplyCurrentDynamicTheme();

        if (RootWindow is MainWindow mainWindow) {
            mainWindow.InitializeCustomTitleBar();
        }
    }

    /// <summary>
    /// Sets the color of the application's primary color brush resource.
    /// </summary>
    public void SetAppPrimaryColorBrushColor(Color newColor) {
        if (Resources.TryGetValue("AppPrimaryColorBrush", out var brushObject) &&
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
    /// Activates the default primary color, which is the system accent color.
    /// </summary>
    public void ActivateDefaultPrimaryColor() {
        SetAppPrimaryColorBrushColor(SystemAccentColor);
    }

    /// <summary>
    /// Applies a dynamic theme color based on color swatches from the current track.
    /// </summary>
    public async void ApplyDynamicThemeFromSwatches(string? lightSwatchId, string? darkSwatchId) {
        if (Services is null) return;
        var settingsService = Services.GetRequiredService<ISettingsService>();
        if (!await settingsService.GetDynamicThemingAsync()) {
            ActivateDefaultPrimaryColor();
            return;
        }

        if (RootWindow?.Content is not FrameworkElement rootElement) {
            ActivateDefaultPrimaryColor();
            return;
        }

        string? swatchToUse = rootElement.ActualTheme == ElementTheme.Dark ? darkSwatchId : lightSwatchId;

        if (!string.IsNullOrEmpty(swatchToUse) && TryParseHexColor(swatchToUse, out Color targetColor)) {
            SetAppPrimaryColorBrushColor(targetColor);
        }
        else {
            ActivateDefaultPrimaryColor();
        }
    }

    /// <summary>
    /// Reapplies the dynamic theme based on the currently playing track.
    /// </summary>
    public void ReapplyCurrentDynamicTheme() {
        if (Services is null) return;
        var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
        if (playbackService.CurrentTrack is not null) {
            ApplyDynamicThemeFromSwatches(
                playbackService.CurrentTrack.LightSwatchId,
                playbackService.CurrentTrack.DarkSwatchId);
        }
        else {
            ActivateDefaultPrimaryColor();
        }
    }

    /// <summary>
    /// Attempts to parse a hexadecimal color string into a Color object.
    /// </summary>
    /// <returns>True if parsing was successful, otherwise false.</returns>
    private bool TryParseHexColor(string hex, out Color color) {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb)) return false;

        switch (hex.Length) {
            case 6:
                color = Color.FromArgb(255, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            case 8:
                color = Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            default:
                return false;
        }
    }

    #endregion

    #region Window Activation and System Integration

    /// <summary>
    /// Handles window activation behavior based on launch arguments and settings.
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
    /// Enqueues tasks to run after the main UI is responsive.
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
    /// Attempts to apply the Mica system backdrop effect to the main window.
    /// </summary>
    /// <returns>True if the backdrop was successfully applied, otherwise false.</returns>
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

    #endregion

    #region Velopack Updates

    /// <summary>
    /// Checks for and applies application updates using Velopack.
    /// </summary>
    private async Task CheckForUpdatesAsync() {
        if (Services is null) return;
            #if DEBUG
                    Debug.WriteLine("[INFO] App: Skipping update check in DEBUG mode.");
                    return;
            #else
        try {
            var updateManager = Services.GetRequiredService<UpdateManager>();
            var newVersion = await updateManager.CheckForUpdatesAsync();

            if (newVersion == null) {
                Debug.WriteLine("[INFO] App: No updates found.");
                return;
            }

            Debug.WriteLine($"[INFO] App: New version {newVersion.TargetFullRelease.Version} found. Downloading...");
            await updateManager.DownloadUpdatesAsync(newVersion);

            Debug.WriteLine("[INFO] App: Update downloaded. Applying and restarting.");
            updateManager.ApplyUpdatesAndRestart(newVersion);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] App: Failed while checking for updates. {ex.Message}");
        }
#endif
    }
    #endregion
}