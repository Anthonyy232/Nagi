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
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application {
    private static Color? _systemAccentColor;
    internal Window? _window;
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
        VelopackApp.Build().Run();
    }

    /// <summary>
    /// Statically initializes the application's services and database.
    /// </summary>
    static App() {
        Services = ConfigureServices();
        InitializeDatabase();
    }

    public static App? CurrentApp { get; private set; }
    public static Window? RootWindow { get; private set; }
    public static IServiceProvider Services { get; }
    public static DispatcherQueue? MainDispatcherQueue { get; private set; }
    public static bool IsExiting { get; set; }

    /// <summary>
    /// Gets the system accent color, with a fallback to a default color.
    /// </summary>
    public static Color SystemAccentColor {
        get {
            if (_systemAccentColor is null) {
                if (Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color) {
                    _systemAccentColor = color;
                }
                else {
                    // Fallback color if the resource is unavailable.
                    _systemAccentColor = Colors.SlateGray;
                }
            }
            return _systemAccentColor.Value;
        }
    }

    /// <summary>
    /// Configures the dependency injection services for the application.
    /// </summary>
    private static IServiceProvider ConfigureServices() {
        var services = new ServiceCollection();

        // App Configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        // Foundational Services
        services.AddSingleton<PathConfiguration>();
        services.AddHttpClient();
        services.AddSingleton(provider => {
            var source = new GithubSource("https://github.com/Anthonyy232/Nagi", null, false);
            return new UpdateManager(source);
        });

        // Database
        services.AddDbContextFactory<MusicDbContext>((serviceProvider, options) => {
            var pathConfig = serviceProvider.GetRequiredService<PathConfiguration>();
            options.UseSqlite($"Data Source={pathConfig.DatabasePath}");
        });

        // Core Application Services
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IMetadataExtractor, TagLibMetadataExtractor>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IWin32InteropService, Win32InteropService>();
        services.AddSingleton<ITrayPopupService, TrayPopupService>();
        services.AddSingleton<IAudioPlayer>(provider => {
            if (MainDispatcherQueue is null)
                throw new InvalidOperationException("MainDispatcherQueue must be initialized before creating AudioPlayerService.");
            return new AudioPlayerService(MainDispatcherQueue);
        });

        // External API Services
        services.AddSingleton<IApiKeyService, ApiKeyService>();
        services.AddSingleton<ILastFmService, LastFmService>();
        services.AddSingleton<ISpotifyService, SpotifyService>();

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
    /// Ensures the application database is created if it does not already exist.
    /// </summary>
    private static void InitializeDatabase() {
        try {
            var dbContextFactory = Services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            using var dbContext = dbContextFactory.CreateDbContext();
            // This ensures the database is created based on the model but will not
            // delete or modify an existing database.
            dbContext.Database.EnsureCreated();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[App] CRITICAL: Failed to initialize database. {ex.Message}");
        }
    }

    /// <summary>
    /// Invoked when the application is launched normally by the end user.
    /// </summary>
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

        // Run non-blocking post-launch tasks.
        _ = CheckForUpdatesAsync();
        EnqueuePostLaunchTasks();
    }

    /// <summary>
    /// Invoked when application execution is being suspended.
    /// </summary>
    private async void OnSuspending(object? sender, SuspendingEventArgs e) {
        var deferral = e.SuspendingOperation.GetDeferral();
        await SaveApplicationStateAsync();
        deferral.Complete();
    }

    /// <summary>
    /// Saves the application's playback state based on user settings.
    /// </summary>
    private async Task SaveApplicationStateAsync() {
        var settingsService = Services.GetRequiredService<ISettingsService>();
        var musicPlaybackService = Services.GetRequiredService<IMusicPlaybackService>();

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

    /// <summary>
    /// Invoked when the main application window is closed.
    /// </summary>
    private async void OnWindowClosed(object sender, WindowEventArgs args) {
        await SaveApplicationStateAsync();

        _micaController?.Dispose();
        _micaController = null;

        _wsdqHelper?.Dispose();
        _wsdqHelper = null;

        if (Services.GetService<TrayIconViewModel>() is IDisposable disposable) {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Handles unhandled exceptions to prevent the application from crashing.
    /// </summary>
    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        Debug.WriteLine($"[App] UNHANDLED EXCEPTION: {e.Exception}");
        // Prevent application crash. For a production app, this should be logged to a file or service.
        e.Handled = true;
    }

    /// <summary>
    /// Checks if music folders are configured and navigates to the appropriate initial page.
    /// </summary>
    public async Task CheckAndNavigateToMainContent() {
        if (RootWindow is null) return;

        var libraryService = Services.GetRequiredService<ILibraryService>();
        bool hasFolders = (await libraryService.GetAllFoldersAsync()).Any();

        if (hasFolders) {
            if (RootWindow.Content is not MainPage) {
                RootWindow.Content = new MainPage();
            }
            // Initialize the view model for the main library page.
            var libraryViewModel = Services.GetRequiredService<LibraryViewModel>();
            await libraryViewModel.InitializeAsync();
        }
        else {
            if (RootWindow.Content is not OnboardingPage) {
                RootWindow.Content = new OnboardingPage();
            }
        }

        // Initialize theme and title bar after content is set.
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
    /// Applies a dynamic theme color based on color swatches.
    /// </summary>
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

    /// <summary>
    /// Reapplies the dynamic theme based on the currently playing track.
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
    /// Attempts to parse a hexadecimal color string into a Color object.
    /// </summary>
    private bool TryParseHexColor(string hex, out Color color) {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb)) return false;

        switch (hex.Length) {
            case 6: // #RRGGBB
                color = Color.FromArgb(255, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            case 8: // #AARRGGBB
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
        if (_window is null) return;

        var settingsService = Services.GetRequiredService<ISettingsService>();
        bool startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
        bool hideToTray = await settingsService.GetHideToTrayEnabledAsync();

        if (isStartupLaunch || startMinimized) {
            if (!hideToTray) {
                WindowActivator.ShowMinimized(_window);
            }
            // If hideToTray is true, the window remains hidden.
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
            try {
                // Initialize services that require an active dispatcher, like SMTC.
                var audioPlayerService = Services.GetRequiredService<IAudioPlayer>();
                audioPlayerService.InitializeSmtc();
            }
            catch (Exception ex) {
                Debug.WriteLine($"[App] Error: Failed to initialize System Media Transport Controls. {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Attempts to apply the Mica system backdrop effect to the main window.
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

    #endregion

    #region Velopack Updates

    /// <summary>
    /// Checks for and applies application updates using Velopack.
    /// </summary>
    private async Task CheckForUpdatesAsync() {
        // Update checks are skipped in debug builds for faster development startup.
        #if DEBUG
            Debug.WriteLine("[App] Skipping update check in DEBUG mode.");
            return;
        #else
        try
        {
            var updateManager = Services.GetRequiredService<UpdateManager>();
            var newVersion = await updateManager.CheckForUpdatesAsync();

            if (newVersion == null)
            {
                Debug.WriteLine("[App] No updates found.");
                return;
            }

            Debug.WriteLine($"[App] New version {newVersion.TargetFullRelease.Version} found. Downloading...");
            await updateManager.DownloadUpdatesAsync(newVersion);

            Debug.WriteLine("[App] Update downloaded. Applying and restarting.");
            updateManager.ApplyUpdatesAndRestart(newVersion);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Error checking for updates: {ex.Message}");
        }
#endif
    }
    #endregion
}