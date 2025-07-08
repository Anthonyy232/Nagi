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

public partial class App : Application {
    private static Color? _systemAccentColor;
    internal Window? _window;
    private MicaController? _micaController;
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

    static App() {
        Services = ConfigureServices();
        InitializeDatabase();
    }

    public App() {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        CoreApplication.Suspending += OnSuspending;
    }

    public static App? CurrentApp { get; private set; }
    public static Window? RootWindow { get; private set; }
    public static IServiceProvider Services { get; }
    public static DispatcherQueue? MainDispatcherQueue { get; private set; }
    public static bool IsExiting { get; set; }

    // Gets the system's accent color, caching it on first access.
    // Provides a fallback color for stability if the resource lookup fails.
    public static Color SystemAccentColor {
        get {
            if (_systemAccentColor is null) {
                if (Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color) {
                    _systemAccentColor = color;
                }
                else {
                    // This fallback is crucial if theme resources are somehow unavailable.
                    Debug.WriteLine("[App] Warning: SystemAccentColor resource not found. Using fallback.");
                    _systemAccentColor = Colors.SlateGray;
                }
            }
            return _systemAccentColor.Value;
        }
    }

    // Configures the dependency injection container for the application.
    private static IServiceProvider ConfigureServices() {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddHttpClient();

        // Velopack Update Manager
        services.AddSingleton(provider => {
            var source = new GithubSource("https://github.com/Anthonyy232/Nagi", null, false);
            return new UpdateManager(source);
        });

        // Services that depend on the UI thread dispatcher.
        // The factory ensures the dispatcher is available before creating the service.
        services.AddSingleton<IAudioPlayer>(provider => {
            if (MainDispatcherQueue is null) {
                throw new InvalidOperationException("MainDispatcherQueue must be initialized before creating AudioPlayerService.");
            }
            return new AudioPlayerService(MainDispatcherQueue);
        });

        // Application services
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

        // Database context
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

    // Ensures the application's database is created on startup.
    private static void InitializeDatabase() {
        try {
            using var dbContext = Services.GetRequiredService<MusicDbContext>();
            dbContext.Database.EnsureCreated();
        }
        catch (Exception ex) {
            // A working database is critical. This failure should be logged prominently.
            Debug.WriteLine($"[App] CRITICAL: Failed to initialize database. {ex.Message}");
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args) {
        // Determine if the app was launched at startup to decide the initial window state.
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

        // Check for updates in the background.
        _ = CheckForUpdatesAsync();

        // Defer non-critical initializations to avoid blocking UI rendering.
        EnqueuePostLaunchTasks();
    }

    // Handles the application suspending event, typically triggered by the OS.
    private async void OnSuspending(object? sender, SuspendingEventArgs e) {
        var deferral = e.SuspendingOperation.GetDeferral();
        await SaveApplicationStateAsync();
        deferral.Complete();
    }

    // Persists application state, such as playback position, before closing or suspending.
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

    // Cleans up resources when the main window is closed.
    private async void OnWindowClosed(object sender, WindowEventArgs args) {
        await SaveApplicationStateAsync();

        _micaController?.Dispose();
        _micaController = null;

        _wsdqHelper?.Dispose();
        _wsdqHelper = null;

        if (Services.GetService<TrayIconViewModel>() is IDisposable disposableTrayViewModel) {
            disposableTrayViewModel.Dispose();
        }
    }

    // Global exception handler to log errors and prevent the application from crashing unexpectedly.
    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        Debug.WriteLine($"[App] UNHANDLED EXCEPTION: {e.Exception}");

        // Marking the exception as handled prevents the app from terminating,
        // which can provide a better user experience for non-fatal errors.
        e.Handled = true;
    }

    // Checks application state and navigates to the appropriate initial page.
    public async Task CheckAndNavigateToMainContent() {
        if (RootWindow is null) return;

        var libraryService = Services.GetRequiredService<ILibraryService>();
        bool hasFolders = (await libraryService.GetAllFoldersAsync()).Any();

        // Navigate to the main application if library folders are configured, otherwise show onboarding.
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

        // Apply the current theme and initialize the title bar for the new content.
        if (RootWindow.Content is FrameworkElement currentContent && RootWindow is MainWindow mainWindow) {
            var settingsService = Services.GetRequiredService<ISettingsService>();
            currentContent.RequestedTheme = await settingsService.GetThemeAsync();
            mainWindow.InitializeCustomTitleBar();
        }
    }

    #region Theming and Color Management

    public void ApplyTheme(ElementTheme themeToApply) {
        if (RootWindow?.Content is not FrameworkElement rootElement) return;

        rootElement.RequestedTheme = themeToApply;
        ReapplyCurrentDynamicTheme();

        if (RootWindow is MainWindow mainWindow) {
            mainWindow.InitializeCustomTitleBar();
        }
    }

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

    public void ActivateDefaultPrimaryColor() {
        SetAppPrimaryColorBrushColor(SystemAccentColor);
    }

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

        string? swatchToUse = rootElement.ActualTheme == ElementTheme.Dark ? darkSwatchId : lightSwatchId;

        if (!string.IsNullOrEmpty(swatchToUse) && TryParseHexColor(swatchToUse, out Color targetColor)) {
            SetAppPrimaryColorBrushColor(targetColor);
        }
        else {
            ActivateDefaultPrimaryColor();
        }
    }

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

    // Parses a 6-digit (RRGGBB) or 8-digit (AARRGGBB) hex color string.
    private bool TryParseHexColor(string hex, out Color color) {
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

    #endregion

    #region Window Activation and System Integration

    // Manages the initial visibility of the main window based on user settings.
    private async Task HandleWindowActivationAsync(bool isStartupLaunch = false) {
        if (_window is null) return;

        var settingsService = Services.GetRequiredService<ISettingsService>();
        bool startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
        bool hideToTray = await settingsService.GetHideToTrayEnabledAsync();

        if (isStartupLaunch || startMinimized) {
            if (hideToTray) {
                // Start hidden; the tray icon will be the only visible UI.
            }
            else {
                // Start minimized to the taskbar.
                WindowActivator.ShowMinimized(_window);
            }
        }
        else {
            // Normal activation.
            _window.Activate();
        }
    }

    // Enqueues tasks to be run after the initial launch sequence is complete.
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

    // Attempts to apply the Mica backdrop material to the main window.
    private bool TrySetMicaBackdrop() {
        if (!MicaController.IsSupported()) return false;

        _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
        _wsdqHelper.EnsureDispatcherQueue();

        var configurationSource = new SystemBackdropConfiguration { IsInputActive = true };

        if (RootWindow?.Content is FrameworkElement rootElement) {
            // Ensure the backdrop theme updates with the app's theme.
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

    private async Task CheckForUpdatesAsync() {
        try {
            var um = Services.GetRequiredService<UpdateManager>();
            var newVersion = await um.CheckForUpdatesAsync();

            if (newVersion == null) {
                Debug.WriteLine("[App] No updates found.");
                return; // No updates available
            }

            Debug.WriteLine($"[App] New version {newVersion.TargetFullRelease.Version} found. Downloading...");
            await um.DownloadUpdatesAsync(newVersion);

            Debug.WriteLine("[App] Update downloaded. Applying and restarting.");
            // This will close the app, apply the update, and restart the new version.
            um.ApplyUpdatesAndRestart(newVersion);
        }
        catch (Exception ex) {
            // It's important to catch exceptions here, otherwise the app may crash on startup.
            Debug.WriteLine($"[App] Error checking for updates: {ex.Message}");
        }
    }

    #endregion
}