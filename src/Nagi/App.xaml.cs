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

    public App() {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        CoreApplication.Suspending += OnSuspending;
        VelopackApp.Build().Run();
    }

    public static App? CurrentApp { get; private set; }
    public static Window? RootWindow => CurrentApp?._window;
    public static IServiceProvider? Services { get; private set; }
    public static DispatcherQueue? MainDispatcherQueue => CurrentApp?._window?.DispatcherQueue;

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

    #region Service Configuration

    private static IServiceProvider ConfigureServices(Window window, DispatcherQueue dispatcherQueue, App appInstance) {
        var services = new ServiceCollection();
        ConfigureCoreServices(services);
        ConfigureUIServices(services, window, dispatcherQueue, appInstance);
        return services.BuildServiceProvider();
    }

    public static void ConfigureCoreServices(IServiceCollection services) {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddSingleton<PathConfiguration>();
        services.AddHttpClient();
        services.AddSingleton(provider => {
            var source = new GithubSource("https://github.com/Anthonyy232/Nagi", null, false);
            return new UpdateManager(source);
        });

        services.AddDbContextFactory<MusicDbContext>((serviceProvider, options) => {
            var pathConfig = serviceProvider.GetRequiredService<PathConfiguration>();
            options.UseSqlite($"Data Source={pathConfig.DatabasePath}");
        });

        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IMetadataExtractor, TagLibMetadataExtractor>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IWin32InteropService, Win32InteropService>();
        services.AddSingleton<IUpdateService, VelopackUpdateService>();

        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryReader>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryWriter>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryScanner>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<IPlaylistService>(provider => provider.GetRequiredService<LibraryService>());

        services.AddSingleton<IApiKeyService, ApiKeyService>();
        services.AddSingleton<ILastFmMetadataService, LastFmMetadataService>();
        services.AddSingleton<ILastFmAuthService, LastFmAuthService>();
        services.AddSingleton<ISpotifyService, SpotifyService>();
        services.AddSingleton<ICredentialLockerService, CredentialLockerService>();
    }

    private static void ConfigureUIServices(IServiceCollection services, Window window, DispatcherQueue dispatcherQueue, App appInstance) {
        services.AddSingleton<IWindowService>(sp => new WinUIWindowService(window, sp.GetRequiredService<IWin32InteropService>()));
        services.AddSingleton<IUIService>(sp => new WinUIUIService(window));
        services.AddSingleton<IDispatcherService>(sp => new WinUIDispatcherService(dispatcherQueue));
        services.AddSingleton<IThemeService>(sp => new WinUIThemeService(appInstance, sp));
        services.AddSingleton<IApplicationLifecycle>(sp => new WinUIApplicationLifecycle(appInstance, sp));
        services.AddSingleton<IAppInfoService, WinUIAppInfoService>();

        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<ITrayPopupService, TrayPopupService>();
        services.AddSingleton<IAudioPlayer>(provider => {
            if (dispatcherQueue is null)
                throw new InvalidOperationException("DispatcherQueue must be available to create AudioPlayerService.");
            return new AudioPlayerService(dispatcherQueue);
        });

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

    #endregion

    private static void InitializeDatabase(IServiceProvider services) {
        try {
            var dbContextFactory = services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
            using var dbContext = dbContextFactory.CreateDbContext();

            // This will create the database if it doesn't exist and apply any pending migrations.
            dbContext.Database.Migrate();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[CRITICAL] App: Failed to initialize or migrate database. {ex.Message}");
        }
    }

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

    private void InitializeWindowAndServices() {
        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        Services = ConfigureServices(_window, _window.DispatcherQueue, this);
        InitializeDatabase(Services);
    }

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

    private async Task InitializeCoreServicesAsync() {
        if (Services is null) return;
        try {
            var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
            await playbackService.InitializeAsync();

            var trayIconVm = Services.GetRequiredService<TrayIconViewModel>();
            await trayIconVm.InitializeAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] App: Failed to initialize services. {ex.Message}");
        }
    }

    private void PerformPostLaunchTasks() {
        // Run startup tasks that don't need to block the UI thread.
        _ = CheckForUpdatesOnStartupAsync();
        EnqueuePostLaunchTasks();
    }

    private async void OnSuspending(object? sender, SuspendingEventArgs e) {
        var deferral = e.SuspendingOperation.GetDeferral();
        if (Services is not null) {
            await SaveApplicationStateAsync(Services);
        }
        deferral.Complete();
    }

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

    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        // Log the exception and prevent the application from crashing.
        Debug.WriteLine($"[FATAL] App: UNHANDLED EXCEPTION: {e.Exception}");
        e.Handled = true;
    }

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

        if (RootWindow.Content is FrameworkElement currentContent && RootWindow is MainWindow mainWindow) {
            var settingsService = Services.GetRequiredService<ISettingsService>();
            currentContent.RequestedTheme = await settingsService.GetThemeAsync();
            mainWindow.InitializeCustomTitleBar();
        }
    }

    #region Theming and Color Management

    internal void ApplyThemeInternal(ElementTheme themeToApply) {
        if (RootWindow?.Content is not FrameworkElement rootElement) return;

        rootElement.RequestedTheme = themeToApply;
        Services?.GetRequiredService<IThemeService>().ReapplyCurrentDynamicTheme();

        if (RootWindow is MainWindow mainWindow) {
            mainWindow.InitializeCustomTitleBar();
        }
    }

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

    public bool TryParseHexColor(string hex, out Color color) {
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

    #region Application Updates

    /// <summary>
    /// Initiates a background check for application updates using the configured update service.
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync() {
        if (Services is null) return;
        var updateService = Services.GetRequiredService<IUpdateService>();
        await updateService.CheckForUpdatesOnStartupAsync();
    }
    #endregion
}