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
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.UI;
using WinRT;
using WinRT.Interop;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;
using Microsoft.Extensions.Configuration;

namespace Nagi;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application {
    private static Color? _systemAccentColor;

    public static Color SystemAccentColor {
        get {
            if (_systemAccentColor == null) {
                // Try to get the color from the application's resources.
                if (Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color) {
                    _systemAccentColor = color;
                }
                else {
                    // Provide a sensible fallback color if the resource isn't available for some reason.
                    Debug.WriteLine("[App] WARNING: Could not find SystemAccentColor resource. Using fallback.");
                    _systemAccentColor = Colors.SlateGray;
                }
            }
            return _systemAccentColor.Value;
        }
    }

    private Window? _window;
    private MicaController? _micaController;
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

    /// <summary>
    /// Gets the singleton instance of the application.
    /// </summary>
    public static App? CurrentApp { get; private set; }

    /// <summary>
    /// Gets the main application window.
    /// </summary>
    public static Window? RootWindow { get; private set; }

    /// <summary>
    /// Gets the dependency injection service provider.
    /// </summary>
    public static IServiceProvider Services { get; }

    /// <summary>
    /// Gets the dispatcher queue for the main UI thread.
    /// </summary>
    public static DispatcherQueue? MainDispatcherQueue { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether the application is in the process of exiting.
    /// This is used to bypass certain behaviors, like hiding to the tray, during a deliberate shutdown.
    /// </summary>
    public static bool IsExiting { get; set; }

    /// <summary>
    /// Initializes the application's service provider and database.
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
    /// Configures the dependency injection container for the application.
    /// </summary>
    private static IServiceProvider ConfigureServices() {
        var services = new ServiceCollection();

        // Application keys
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        // HTTP Client
        services.AddHttpClient();

        // Core Services
        services.AddSingleton<IAudioPlayer>(provider => {
            if (MainDispatcherQueue == null)
                throw new InvalidOperationException("MainDispatcherQueue must be initialized before creating AudioPlayerService.");
            return new AudioPlayerService(MainDispatcherQueue);
        });
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IMetadataExtractor, TagLibMetadataExtractor>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IApiKeyService, ApiKeyService>();
        services.AddSingleton<ILastFmService, LastFmService>();
        services.AddSingleton<ISpotifyService, SpotifyService>();

        // Database
        services.AddTransient<MusicDbContext>();

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

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Ensures that the application's database is created if it does not already exist.
    /// </summary>
    private static void InitializeDatabase() {
        try {
            using var dbContext = Services.GetRequiredService<MusicDbContext>();
            dbContext.RecreateDatabase();
            dbContext.Database.EnsureCreated();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[App] CRITICAL: Failed to initialize database. {ex.Message}");
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args) {
        bool isStartupLaunch = Environment.GetCommandLineArgs().Any(arg =>
            arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow();
        RootWindow = _window;
        MainDispatcherQueue = _window.DispatcherQueue;
        _window.Closed += OnWindowClosed;

        var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
        try {
            await playbackService.InitializeAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[App] CRITICAL: Failed to initialize MusicPlaybackService. {ex.Message}");
        }

        TrySetMicaBackdrop();
        ReapplyCurrentDynamicTheme();
        await CheckAndNavigateToMainContent();

        // Activate the window according to user settings and launch context.
        await HandleWindowActivationAsync(isStartupLaunch);

        EnqueuePostLaunchTasks();
    }

    /// <summary>
    /// Handles the application suspending event, a reliable trigger for saving application state.
    /// </summary>
    private async void OnSuspending(object? sender, SuspendingEventArgs e) {
        var deferral = e.SuspendingOperation.GetDeferral();
        await SaveApplicationStateAsync();
        deferral.Complete();
    }

    /// <summary>
    /// Persists application state, such as playback position, based on user settings.
    /// </summary>
    private async Task SaveApplicationStateAsync() {
        if (Services.GetService<ISettingsService>() is not { } settingsService ||
            Services.GetService<IMusicPlaybackService>() is not { } musicPlaybackService) {
            Debug.WriteLine("[App] ERROR: Could not resolve required services to save application state.");
            return;
        }

        try {
            if (await settingsService.GetRestorePlaybackStateEnabledAsync()) {
                await musicPlaybackService.SavePlaybackStateAsync();
            }
            else {
                await settingsService.ClearPlaybackStateAsync();
            }
        }
        catch (Exception ex) {
            Debug.WriteLine($"[App] ERROR: Failed to save or clear playback state. {ex.Message}");
        }
    }

    /// <summary>
    /// Cleans up resources when the main window is closed.
    /// </summary>
    private async void OnWindowClosed(object sender, WindowEventArgs args) {
        await SaveApplicationStateAsync();

        _micaController?.Dispose();
        _micaController = null;

        if (Services.GetService<TrayIconViewModel>() is IDisposable disposableTrayViewModel) {
            disposableTrayViewModel.Dispose();
        }
    }

    /// <summary>
    /// Central handler for unhandled exceptions.
    /// </summary>
    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        Debug.WriteLine($"[App] UNHANDLED EXCEPTION: {e.Exception}");
        e.Handled = true; // Prevents the application from crashing.
    }

    /// <summary>
    /// Checks if the library has been configured and navigates to the appropriate initial page.
    /// </summary>
    public async Task CheckAndNavigateToMainContent() {
        if (RootWindow == null) return;

        var libraryService = Services.GetRequiredService<ILibraryService>();
        var hasFolders = (await libraryService.GetAllFoldersAsync()).Any();

        if (hasFolders) {
            if (RootWindow.Content is not MainPage) RootWindow.Content = new MainPage();
            var libraryViewModel = Services.GetRequiredService<LibraryViewModel>();
            await libraryViewModel.InitializeAndStartBackgroundScanAsync();
        }
        else {
            if (RootWindow.Content is not OnboardingPage) RootWindow.Content = new OnboardingPage();
        }

        if (RootWindow.Content is FrameworkElement currentContent && RootWindow is MainWindow mainWindow) {
            var settingsService = Services.GetRequiredService<ISettingsService>();
            currentContent.RequestedTheme = await settingsService.GetThemeAsync();
            mainWindow.InitializeCustomTitleBar();
        }
    }

    #region Theming and Color Management

    /// <summary>
    /// Applies the specified theme to the application's root visual element.
    /// </summary>
    /// <param name="themeToApply">The theme to apply (Light, Dark, or Default).</param>
    public void ApplyTheme(ElementTheme themeToApply) {
        if (RootWindow?.Content is not FrameworkElement rootElement) return;

        rootElement.RequestedTheme = themeToApply;
        ReapplyCurrentDynamicTheme();

        if (RootWindow is MainWindow mainWindow) {
            mainWindow.InitializeCustomTitleBar();
        }
    }

    /// <summary>
    /// Sets the color of the primary application brush resource.
    /// </summary>
    /// <param name="newColor">The new color to apply.</param>
    public void SetAppPrimaryColorBrushColor(Color newColor) {
        if (Resources.TryGetValue("AppPrimaryColorBrush", out var brushObject) &&
            brushObject is SolidColorBrush appPrimaryColorBrush) {
            if (appPrimaryColorBrush.Color != newColor) {
                appPrimaryColorBrush.Color = newColor;
            }
        }
        else {
            Debug.WriteLine("[App] CRITICAL: AppPrimaryColorBrush resource not found or is not a SolidColorBrush.");
        }
    }

    /// <summary>
    /// Resets the primary application color to the system's accent color.
    /// </summary>
    public void ActivateDefaultPrimaryColor() {
        SetAppPrimaryColorBrushColor(SystemAccentColor);
    }

    /// <summary>
    /// Applies a dynamic theme color based on the provided color swatches and the current app theme.
    /// </summary>
    /// <param name="lightSwatchId">The hex color string for the light theme.</param>
    /// <param name="darkSwatchId">The hex color string for the dark theme.</param>
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

        var currentTheme = rootElement.ActualTheme;
        var swatchToUse = currentTheme == ElementTheme.Dark ? darkSwatchId : lightSwatchId;

        if (!string.IsNullOrEmpty(swatchToUse) && TryParseHexColor(swatchToUse, out var targetColor)) {
            SetAppPrimaryColorBrushColor(targetColor);
        }
        else {
            ActivateDefaultPrimaryColor();
        }
    }

    /// <summary>
    /// Reapplies the dynamic theme based on the currently playing track. If no track is playing,
    /// it reverts to the default system accent color.
    /// </summary>
    public void ReapplyCurrentDynamicTheme() {
        var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
        if (playbackService.CurrentTrack != null) {
            ApplyDynamicThemeFromSwatches(
                playbackService.CurrentTrack.LightSwatchId,
                playbackService.CurrentTrack.DarkSwatchId);
        }
        else {
            ActivateDefaultPrimaryColor();
        }
    }

    /// <summary>
    /// Attempts to parse a hexadecimal color string into a <see cref="Color"/>.
    /// Supports RRGGBB and AARRGGBB formats.
    /// </summary>
    private bool TryParseHexColor(string hex, out Color color) {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (uint.TryParse(hex, NumberStyles.HexNumber, null, out var argb)) {
            if (hex.Length == 6) // RRGGBB
            {
                color = Color.FromArgb(255, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            }

            if (hex.Length == 8) // AARRGGBB
            {
                color = Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Window Activation and System Integration

    /// <summary>
    /// Determines how the main window should be displayed on startup based on user settings.
    /// </summary>
    /// <param name="isStartupLaunch">Indicates if the app was launched via a startup task.</param>
    private async Task HandleWindowActivationAsync(bool isStartupLaunch = false) {
        if (_window == null) return;

        var settingsService = Services.GetRequiredService<ISettingsService>();
        var startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
        var hideToTray = await settingsService.GetHideToTrayEnabledAsync();

        // If launched at startup, or if the user has configured "Start Minimized",
        // we will not activate the window normally.
        if (isStartupLaunch || startMinimized) {
            if (hideToTray) {
                // Start minimized to the system tray by not activating the window.
                // The TrayIconViewModel ensures the icon is visible.
                Debug.WriteLine("[App] Starting minimized to tray.");
            }
            else {
                // Start minimized to the taskbar using a flicker-free method.
                Debug.WriteLine("[App] Starting minimized to taskbar.");
                WindowActivator.ShowMinimized(_window);
            }
        }
        else {
            // Standard launch: Activate and show the window normally.
            Debug.WriteLine("[App] Starting normally.");
            _window.Activate();
        }
    }

    /// <summary>
    /// Enqueues tasks to be run after the application has launched and the UI is active.
    /// </summary>
    private void EnqueuePostLaunchTasks() {
        if (MainDispatcherQueue == null) return;

        MainDispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => {
            try {
                var audioPlayerService = Services.GetRequiredService<IAudioPlayer>();
                audioPlayerService.InitializeSmtc();
            }
            catch (Exception ex) {
                Debug.WriteLine($"[App] ERROR: Failed to initialize SMTC. {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Attempts to apply the Mica backdrop material to the main window.
    /// </summary>
    private bool TrySetMicaBackdrop() {
        if (!MicaController.IsSupported()) return false;

        _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
        _wsdqHelper.EnsureWindowsSystemDispatcherQueueController();

        var configurationSource = new SystemBackdropConfiguration { IsInputActive = true };

        if (RootWindow?.Content is FrameworkElement rootElement) {
            Action updateTheme = () => {
                configurationSource.Theme = rootElement.ActualTheme switch {
                    ElementTheme.Dark => SystemBackdropTheme.Dark,
                    ElementTheme.Light => SystemBackdropTheme.Light,
                    _ => SystemBackdropTheme.Default
                };
            };
            rootElement.ActualThemeChanged += (s, e) => updateTheme();
            updateTheme();
        }

        _micaController = new MicaController();
        _micaController.SetSystemBackdropConfiguration(configurationSource);

        if (RootWindow != null) {
            _micaController.AddSystemBackdropTarget(RootWindow.As<ICompositionSupportsSystemBackdrop>());
            return true;
        }

        return false;
    }

    /// <summary>
    /// Private helper class to encapsulate Win32 API calls for window management.
    /// </summary>
    private static class WindowActivator {
        // Shows a window.
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_SHOWMINIMIZED = 2;

        /// <summary>
        /// Shows the specified window in a minimized state without flicker.
        /// </summary>
        public static void ShowMinimized(Window window) {
            var windowHandle = WindowNative.GetWindowHandle(window);
            ShowWindow(windowHandle, SW_SHOWMINIMIZED);
        }
    }

    #endregion
}