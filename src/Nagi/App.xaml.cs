using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.UI;
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
using WinRT;
using WinRT.Interop;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace Nagi;

/// <summary>
///     Provides application-specific behavior, manages application lifecycle,
///     and sets up dependency injection.
/// </summary>
public partial class App : Application {
    private static Color? _systemAccentColor;
    private MicaController? _micaController;
    private Window? _window;
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

    /// <summary>
    ///     Configures services and initializes the database.
    /// </summary>
    static App() {
        Services = ConfigureServices();
        InitializeDatabase();
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="App" /> class.
    /// </summary>
    public App() {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        CoreApplication.Suspending += OnSuspending;
    }

    /// <summary>
    ///     Gets the application's singleton instance.
    /// </summary>
    public static App? CurrentApp { get; private set; }

    /// <summary>
    ///     Gets the main application window.
    /// </summary>
    public static Window? RootWindow { get; private set; }

    /// <summary>
    ///     Gets the dependency injection service provider.
    /// </summary>
    public static IServiceProvider Services { get; }

    /// <summary>
    ///     Gets the dispatcher queue for the main UI thread.
    /// </summary>
    public static DispatcherQueue? MainDispatcherQueue { get; private set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the application is performing a deliberate exit.
    ///     This helps bypass certain behaviors, like hiding to the tray, during shutdown.
    /// </summary>
    public static bool IsExiting { get; set; }

    /// <summary>
    ///     Gets the user's system accent color, with a fallback.
    /// </summary>
    public static Color SystemAccentColor {
        get {
            if (_systemAccentColor is null) {
                if (Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color) {
                    _systemAccentColor = color;
                }
                else {
                    Debug.WriteLine("[App] WARNING: SystemAccentColor resource not found. Using fallback.");
                    _systemAccentColor = Colors.SlateGray;
                }
            }

            return _systemAccentColor.Value;
        }
    }

    /// <summary>
    ///     Sets up the dependency injection container with all application services and view models.
    /// </summary>
    private static IServiceProvider ConfigureServices() {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", false, true)
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddHttpClient();

        services.AddSingleton<IAudioPlayer>(provider => {
            if (MainDispatcherQueue is null)
                throw new InvalidOperationException(
                    "MainDispatcherQueue must be initialized before creating AudioPlayerService.");
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

        services.AddTransient<MusicDbContext>();

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

    private static void InitializeDatabase() {
        try {
            using var dbContext = Services.GetRequiredService<MusicDbContext>();
            dbContext.Database.EnsureCreated();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[App] CRITICAL: Failed to initialize database. {ex.Message}");
        }
    }

    /// <summary>
    ///     Invoked when the application is launched. Responsible for window creation,
    ///     initialization, and activation.
    /// </summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args) {
        var isStartupLaunch = Environment.GetCommandLineArgs().Any(arg =>
            arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow();
        RootWindow = _window;
        MainDispatcherQueue = _window.DispatcherQueue;
        _window.Closed += OnWindowClosed;

        TrySetMicaBackdrop();
        ReapplyCurrentDynamicTheme();

        var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
        try {
            await playbackService.InitializeAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[App] CRITICAL: Failed to initialize MusicPlaybackService. {ex.Message}");
        }

        await CheckAndNavigateToMainContent();
        await HandleWindowActivationAsync(isStartupLaunch);

        EnqueuePostLaunchTasks();
    }

    private async void OnSuspending(object? sender, SuspendingEventArgs e) {
        var deferral = e.SuspendingOperation.GetDeferral();
        await SaveApplicationStateAsync();
        deferral.Complete();
    }

    /// <summary>
    ///     Saves critical application state, such as the current playback queue and position,
    ///     before the application closes or is suspended.
    /// </summary>
    private async Task SaveApplicationStateAsync() {
        if (Services.GetService<ISettingsService>() is not { } settingsService ||
            Services.GetService<IMusicPlaybackService>() is not { } musicPlaybackService) {
            Debug.WriteLine("[App] ERROR: Could not resolve services to save application state.");
            return;
        }

        try {
            if (await settingsService.GetRestorePlaybackStateEnabledAsync())
                await musicPlaybackService.SavePlaybackStateAsync();
            else
                await settingsService.ClearPlaybackStateAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[App] ERROR: Failed to save or clear playback state. {ex.Message}");
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args) {
        await SaveApplicationStateAsync();

        _micaController?.Dispose();
        _micaController = null;

        if (Services.GetService<TrayIconViewModel>() is IDisposable disposableTrayViewModel)
            disposableTrayViewModel.Dispose();
    }

    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        // It's often better to log the exception and attempt to continue
        // rather than letting the app crash.
        Debug.WriteLine($"[App] UNHANDLED EXCEPTION: {e.Exception}");
        e.Handled = true;
    }

    /// <summary>
    ///     Navigates to the appropriate initial page (Onboarding or Main) based on
    ///     whether music folders have been configured. Also initializes the theme and title bar.
    /// </summary>
    public async Task CheckAndNavigateToMainContent() {
        if (RootWindow is null) return;

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
    ///     Applies the specified theme to the application's root element.
    /// </summary>
    /// <param name="themeToApply">The theme to apply (Light, Dark, or Default).</param>
    public void ApplyTheme(ElementTheme themeToApply) {
        if (RootWindow?.Content is not FrameworkElement rootElement) return;

        rootElement.RequestedTheme = themeToApply;
        ReapplyCurrentDynamicTheme();

        if (RootWindow is MainWindow mainWindow) mainWindow.InitializeCustomTitleBar();
    }

    /// <summary>
    ///     Sets the color of the application's primary color brush.
    /// </summary>
    public void SetAppPrimaryColorBrushColor(Color newColor) {
        if (Resources.TryGetValue("AppPrimaryColorBrush", out var brushObject) &&
            brushObject is SolidColorBrush appPrimaryColorBrush) {
            if (appPrimaryColorBrush.Color != newColor) appPrimaryColorBrush.Color = newColor;
        }
        else {
            Debug.WriteLine("[App] CRITICAL: AppPrimaryColorBrush resource not found.");
        }
    }

    /// <summary>
    ///     Resets the application's primary color to the system accent color.
    /// </summary>
    public void ActivateDefaultPrimaryColor() {
        SetAppPrimaryColorBrushColor(SystemAccentColor);
    }

    /// <summary>
    ///     Applies a dynamic theme based on color swatches from the current track's album art.
    /// </summary>
    /// <param name="lightSwatchId">The hex color string for the light theme swatch.</param>
    /// <param name="darkSwatchId">The hex color string for the dark theme swatch.</param>
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

        var swatchToUse = rootElement.ActualTheme == ElementTheme.Dark ? darkSwatchId : lightSwatchId;

        if (!string.IsNullOrEmpty(swatchToUse) && TryParseHexColor(swatchToUse, out var targetColor))
            SetAppPrimaryColorBrushColor(targetColor);
        else
            ActivateDefaultPrimaryColor();
    }

    /// <summary>
    ///     Reapplies the dynamic theme for the currently playing track, or the default
    ///     theme if no track is playing.
    /// </summary>
    public void ReapplyCurrentDynamicTheme() {
        var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
        if (playbackService.CurrentTrack != null)
            ApplyDynamicThemeFromSwatches(
                playbackService.CurrentTrack.LightSwatchId,
                playbackService.CurrentTrack.DarkSwatchId);
        else
            ActivateDefaultPrimaryColor();
    }

    /// <summary>
    ///     Parses a 6-digit (RRGGBB) or 8-digit (AARRGGBB) hex string into a Color object.
    /// </summary>
    private bool TryParseHexColor(string hex, out Color color) {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (!uint.TryParse(hex, NumberStyles.HexNumber, null, out var argb)) return false;

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

    #endregion

    #region Window Activation and System Integration

    /// <summary>
    ///     Determines how the window should be shown on startup based on user settings
    ///     (e.g., start normally, minimized, or hidden to the tray).
    /// </summary>
    private async Task HandleWindowActivationAsync(bool isStartupLaunch = false) {
        if (_window is null) return;

        var settingsService = Services.GetRequiredService<ISettingsService>();
        var startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
        var hideToTray = await settingsService.GetHideToTrayEnabledAsync();

        if (isStartupLaunch || startMinimized) {
            if (hideToTray) {
                Debug.WriteLine("[App] Starting hidden in tray.");
                _window.AppWindow.Hide();
            }
            else {
                Debug.WriteLine("[App] Starting minimized to taskbar.");
                WindowActivator.ShowMinimized(_window);
            }
        }
        else {
            Debug.WriteLine("[App] Starting normally.");
            _window.Activate();
        }
    }

    /// <summary>
    ///     Enqueues tasks that should run after the main UI is initialized and visible,
    ///     such as setting up the System Media Transport Controls (SMTC).
    /// </summary>
    private void EnqueuePostLaunchTasks() {
        MainDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Normal, () => {
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
    ///     Attempts to apply the Mica backdrop material to the main window if supported by the OS.
    /// </summary>
    private bool TrySetMicaBackdrop() {
        if (!MicaController.IsSupported()) return false;

        _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
        _wsdqHelper.EnsureWindowsSystemDispatcherQueueController();

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

        if (RootWindow != null) {
            _micaController.AddSystemBackdropTarget(RootWindow.As<ICompositionSupportsSystemBackdrop>());
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Provides P/Invoke methods for controlling window states not directly available in WinUI.
    /// </summary>
    private static class WindowActivator {
        private const int SW_SHOWMINIMIZED = 2;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static void ShowMinimized(Window window) {
            var windowHandle = WindowNative.GetWindowHandle(window);
            ShowWindow(windowHandle, SW_SHOWMINIMIZED);
        }
    }

    #endregion
}