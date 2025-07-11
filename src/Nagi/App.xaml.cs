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
/// Represents the main application class.
/// </summary>
public partial class App : Application {
    private static Color? _systemAccentColor;
    internal Window? _window;
    private MicaController? _micaController;
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

    /// <summary>
    /// Initializes the static members of the App class.
    /// Sets up dependency injection and initializes the database.
    /// </summary>
    static App() {
        Services = ConfigureServices();
        InitializeDatabase();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="App"/> class.
    /// </summary>
    public App() {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        CoreApplication.Suspending += OnSuspending;
    }

    /// <summary>
    /// Gets the current application instance.
    /// </summary>
    public static App? CurrentApp { get; private set; }

    /// <summary>
    /// Gets the root window of the application.
    /// </summary>
    public static Window? RootWindow { get; private set; }

    /// <summary>
    /// Gets the service provider for dependency injection.
    /// </summary>
    public static IServiceProvider Services { get; }

    /// <summary>
    /// Gets the main dispatcher queue for the UI thread.
    /// </summary>
    public static DispatcherQueue? MainDispatcherQueue { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether the application is in the process of exiting.
    /// </summary>
    public static bool IsExiting { get; set; }

    /// <summary>
    /// Gets the system accent color. If not found, a fallback color is used.
    /// </summary>
    public static Color SystemAccentColor {
        get {
            if (_systemAccentColor is null) {
                if (Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color) {
                    _systemAccentColor = color;
                }
                else {
                    // Fallback to a default color if system accent color resource is not found.
                    _systemAccentColor = Colors.SlateGray;
                }
            }
            return _systemAccentColor.Value;
        }
    }

    /// <summary>
    /// Configures the dependency injection services for the application.
    /// </summary>
    /// <returns>An <see cref="IServiceProvider"/> containing the configured services.</returns>
    private static IServiceProvider ConfigureServices() {
        var services = new ServiceCollection();

        // Centralized Path Configuration (Singleton)
        services.AddSingleton<PathConfiguration>();

        // App Configuration from appsettings.json
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        // HTTP Client Factory for API services
        services.AddHttpClient();

        // Velopack Update Manager for application updates
        services.AddSingleton(provider => {
            var source = new GithubSource("https://github.com/Anthonyy232/Nagi", null, false);
            return new UpdateManager(source);
        });

        // Services that depend on the UI thread dispatcher must be created after MainDispatcherQueue is initialized.
        services.AddSingleton<IAudioPlayer>(provider => {
            if (MainDispatcherQueue is null) {
                throw new InvalidOperationException("MainDispatcherQueue must be initialized before creating AudioPlayerService.");
            }
            return new AudioPlayerService(MainDispatcherQueue);
        });

        // Application core services
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IMetadataExtractor, TagLibMetadataExtractor>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IWin32InteropService, Win32InteropService>();
        services.AddSingleton<ITrayPopupService, TrayPopupService>();

        // External API services
        services.AddSingleton<IApiKeyService, ApiKeyService>();
        services.AddSingleton<ILastFmService, LastFmService>();
        services.AddSingleton<ISpotifyService, SpotifyService>();

        // Database context factory for managing database connections
        services.AddDbContextFactory<MusicDbContext>((serviceProvider, options) => {
            var pathConfig = serviceProvider.GetRequiredService<PathConfiguration>();
            options.UseSqlite($"Data Source={pathConfig.DatabasePath}");
        });

        // ViewModels for UI data binding
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

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Initializes the application database by ensuring its creation.
    /// </summary>
    private static void InitializeDatabase() {
        try {
            // Use the factory to create a context for this one-time operation.
            var dbContextFactory = Services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            using var dbContext = dbContextFactory.CreateDbContext();
            dbContext.Database.EnsureCreated();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[App] CRITICAL: Failed to initialize database. {ex.Message}");
        }
    }

    /// <summary>
    /// Called when the application is launched.
    /// Configures the main window, initializes services, and navigates to initial content.
    /// </summary>
    /// <param name="args">Event arguments for the launch activation.</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args) {
        bool isStartupLaunch = Environment.GetCommandLineArgs()
            .Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow();
        RootWindow = _window;
        MainDispatcherQueue = _window.DispatcherQueue;
        _window.Closed += OnWindowClosed;

        TrySetMicaBackdrop();
        ReapplyCurrentDynamicTheme();

        try {
            var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
            await playbackService.InitializeAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[App] Error: Failed to initialize MusicPlaybackService. {ex.Message}");
        }

        await CheckAndNavigateToMainContent();
        await HandleWindowActivationAsync(isStartupLaunch);

        // Initiate update check without blocking the UI.
        _ = CheckForUpdatesAsync();
        EnqueuePostLaunchTasks();
    }

    /// <summary>
    /// Called when the application is suspending.
    /// Saves application state.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">Event arguments for the suspending operation.</param>
    private async void OnSuspending(object? sender, SuspendingEventArgs e) {
        var deferral = e.SuspendingOperation.GetDeferral();
        await SaveApplicationStateAsync();
        deferral.Complete();
    }

    /// <summary>
    /// Saves or clears the application's playback state based on user settings.
    /// </summary>
    private async Task SaveApplicationStateAsync() {
        if (Services.GetService<ISettingsService>() is { } settingsService &&
            Services.GetService<IMusicPlaybackService>() is { } musicPlaybackService) {
            try {
                if (await settingsService.GetRestorePlaybackStateEnabledAsync()) {
                    await musicPlaybackService.SavePlaybackStateAsync();
                }
                else {
                    await settingsService.ClearPlaybackStateAsync();
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[App] Error: Failed to save or clear playback state. {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Called when the main application window is closed.
    /// Saves application state and disposes of resources.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="args">Event arguments for the window closed event.</param>
    private async void OnWindowClosed(object sender, WindowEventArgs args) {
        await SaveApplicationStateAsync();

        _micaController?.Dispose();
        _micaController = null;

        _wsdqHelper?.Dispose();
        _wsdqHelper = null;

        // Dispose of the tray icon view model if it implements IDisposable.
        if (Services.GetService<TrayIconViewModel>() is IDisposable disposableTrayViewModel) {
            disposableTrayViewModel.Dispose();
        }
    }

    /// <summary>
    /// Handles unhandled exceptions that occur during application execution.
    /// Logs the exception and marks it as handled to prevent application crash.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">Event arguments containing the unhandled exception.</param>
    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        Debug.WriteLine($"[App] UNHANDLED EXCEPTION: {e.Exception}");
        // Mark the exception as handled to prevent the application from crashing.
        e.Handled = true;
    }

    /// <summary>
    /// Checks if music folders are configured and navigates to the appropriate initial page.
    /// If folders exist, it navigates to the main page; otherwise, to the onboarding page.
    /// </summary>
    public async Task CheckAndNavigateToMainContent() {
        if (RootWindow is null) return;

        var libraryService = Services.GetRequiredService<ILibraryService>();
        bool hasFolders = (await libraryService.GetAllFoldersAsync()).Any();

        if (hasFolders) {
            if (RootWindow.Content is not MainPage) {
                RootWindow.Content = new MainPage();
            }
            var libraryViewModel = Services.GetRequiredService<LibraryViewModel>();
            await libraryViewModel.InitializeAndStartBackgroundScanAsync();
        }
        else {
            if (RootWindow.Content is not OnboardingPage) {
                RootWindow.Content = new OnboardingPage();
            }
        }

        // Apply theme and initialize custom title bar after content is set.
        if (RootWindow.Content is FrameworkElement currentContent && RootWindow is MainWindow mainWindow) {
            var settingsService = Services.GetRequiredService<ISettingsService>();
            currentContent.RequestedTheme = await settingsService.GetThemeAsync();
            mainWindow.InitializeCustomTitleBar();
        }
    }

    #region Theming and Color Management

    /// <summary>
    /// Applies the specified theme to the application's root content.
    /// </summary>
    /// <param name="themeToApply">The <see cref="ElementTheme"/> to apply.</param>
    public void ApplyTheme(ElementTheme themeToApply) {
        if (RootWindow?.Content is not FrameworkElement rootElement) return;

        rootElement.RequestedTheme = themeToApply;
        ReapplyCurrentDynamicTheme();

        if (RootWindow is MainWindow mainWindow) {
            mainWindow.InitializeCustomTitleBar();
        }
    }

    /// <summary>
    /// Sets the color of the "AppPrimaryColorBrush" resource.
    /// </summary>
    /// <param name="newColor">The new <see cref="Color"/> to set.</param>
    public void SetAppPrimaryColorBrushColor(Color newColor) {
        if (Resources.TryGetValue("AppPrimaryColorBrush", out var brushObject) &&
            brushObject is SolidColorBrush appPrimaryColorBrush) {
            if (appPrimaryColorBrush.Color != newColor) {
                appPrimaryColorBrush.Color = newColor;
            }
        }
        else {
            Debug.WriteLine("[App] Critical: AppPrimaryColorBrush resource not found.");
        }
    }

    /// <summary>
    /// Activates the default primary color, which is the system accent color.
    /// </summary>
    public void ActivateDefaultPrimaryColor() {
        SetAppPrimaryColorBrushColor(SystemAccentColor);
    }

    /// <summary>
    /// Applies a dynamic theme color based on provided swatch IDs for light and dark modes.
    /// If dynamic theming is disabled or swatches are invalid, the default primary color is used.
    /// </summary>
    /// <param name="lightSwatchId">The hexadecimal color string for light theme.</param>
    /// <param name="darkSwatchId">The hexadecimal color string for dark theme.</param>
    public async void ApplyDynamicThemeFromSwatches(string? lightSwatchId, string? darkSwatchId) {
        var settingsService = Services.GetRequiredService<ISettingsService>();
        if (!await settingsService.GetDynamicThemingAsync()) {
            ActivateDefaultPrimaryColor();
            return;
        }

        if (RootWindow?.Content is not FrameworkElement rootElement) {
            ActivateDefaultPrimaryColor();
            return;
        }

        // Determine which swatch to use based on the current effective theme.
        string? swatchToUse = rootElement.ActualTheme == ElementTheme.Dark ? darkSwatchId : lightSwatchId;

        if (!string.IsNullOrEmpty(swatchToUse) && TryParseHexColor(swatchToUse, out Color targetColor)) {
            SetAppPrimaryColorBrushColor(targetColor);
        }
        else {
            ActivateDefaultPrimaryColor();
        }
    }

    /// <summary>
    /// Reapplies the current dynamic theme based on the currently playing track's swatches,
    /// or activates the default primary color if no track is playing.
    /// </summary>
    public void ReapplyCurrentDynamicTheme() {
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
    /// Attempts to parse a hexadecimal color string into a <see cref="Color"/> object.
    /// Supports RRGGBB and AARRGGBB formats.
    /// </summary>
    /// <param name="hex">The hexadecimal string to parse (e.g., "#RRGGBB" or "AARRGGBB").</param>
    /// <param name="color">When this method returns, contains the parsed Color, if the parsing succeeded.</param>
    /// <returns><c>true</c> if the hex string was successfully parsed; otherwise, <c>false</c>.</returns>
    private bool TryParseHexColor(string hex, out Color color) {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb)) return false;

        switch (hex.Length) {
            case 6: // RRGGBB format assumes full opacity
                color = Color.FromArgb(255, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            case 8: // AARRGGBB format
                color = Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            default:
                return false;
        }
    }

    #endregion

    #region Window Activation and System Integration

    /// <summary>
    /// Handles window activation behavior based on launch arguments and user settings.
    /// Controls whether the window starts minimized, hidden to tray, or activated normally.
    /// </summary>
    /// <param name="isStartupLaunch">True if the application was launched at system startup.</param>
    private async Task HandleWindowActivationAsync(bool isStartupLaunch = false) {
        if (_window is null) return;

        var settingsService = Services.GetRequiredService<ISettingsService>();
        bool startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
        bool hideToTray = await settingsService.GetHideToTrayEnabledAsync();

        if (isStartupLaunch || startMinimized) {
            if (hideToTray) {
                // Application starts hidden, typically into the system tray.
            }
            else {
                // Show window minimized if not hidden to tray.
                WindowActivator.ShowMinimized(_window);
            }
        }
        else {
            // Activate window normally.
            _window.Activate();
        }
    }

    /// <summary>
    /// Enqueues post-launch tasks onto the main dispatcher queue.
    /// This includes initializing System Media Transport Controls (SMTC).
    /// </summary>
    private void EnqueuePostLaunchTasks() {
        MainDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Normal, () => {
            try {
                var audioPlayerService = Services.GetRequiredService<IAudioPlayer>();
                audioPlayerService.InitializeSmtc();
            }
            catch (Exception ex) {
                Debug.WriteLine($"[App] Error: Failed to initialize System Media Transport Controls (SMTC). {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Attempts to apply the Mica system backdrop effect to the main window.
    /// </summary>
    /// <returns><c>true</c> if Mica backdrop was successfully applied; otherwise, <c>false</c>.</returns>
    private bool TrySetMicaBackdrop() {
        // Check if Mica backdrop is supported on the current system.
        if (!MicaController.IsSupported()) return false;

        _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
        _wsdqHelper.EnsureDispatcherQueue();

        // Configure the backdrop's behavior based on input activity and theme.
        var configurationSource = new SystemBackdropConfiguration { IsInputActive = true };

        if (RootWindow?.Content is FrameworkElement rootElement) {
            // Update the backdrop theme when the application's actual theme changes.
            void UpdateTheme() {
                configurationSource.Theme = rootElement.ActualTheme switch {
                    ElementTheme.Dark => SystemBackdropTheme.Dark,
                    ElementTheme.Light => SystemBackdropTheme.Light,
                    _ => SystemBackdropTheme.Default
                };
            }

            rootElement.ActualThemeChanged += (s, e) => UpdateTheme();
            UpdateTheme(); // Set initial theme.
        }

        _micaController = new MicaController();
        _micaController.SetSystemBackdropConfiguration(configurationSource);

        if (RootWindow is not null) {
            // Apply the Mica backdrop to the main window.
            _micaController.AddSystemBackdropTarget(RootWindow.As<ICompositionSupportsSystemBackdrop>());
            return true;
        }

        return false;
    }

    #endregion

    #region Velopack Updates

    /// <summary>
    /// Checks for and applies application updates using Velopack.
    /// This method is skipped in DEBUG builds.
    /// </summary>
    private async Task CheckForUpdatesAsync() {
        #if DEBUG
            Debug.WriteLine("[App] Skipping update check in DEBUG mode.");
        #else
            try 
            {
                var um = Services.GetRequiredService<UpdateManager>();
                var newVersion = await um.CheckForUpdatesAsync();

                if (newVersion == null) 
                {
                    Debug.WriteLine("[App] No updates found.");
                    return;
                }

                Debug.WriteLine($"[App] New version {newVersion.TargetFullRelease.Version} found. Downloading...");
                await um.DownloadUpdatesAsync(newVersion);

                Debug.WriteLine("[App] Update downloaded. Applying and restarting.");
                um.ApplyUpdatesAndRestart(newVersion);
            }
            catch (Exception ex) 
            {
                Debug.WriteLine($"[App] Error checking for updates: {ex.Message}");
            }
        #endif
    }

    #endregion
}