using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.UI;
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
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace Nagi;

public partial class App : Application {
    private static readonly Color DefaultAccentColor = Color.FromArgb(255, 96, 198, 137);
    private MicaController? _micaController;
    private Window? _window;
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

    /// <summary>
    /// Gets or sets a value indicating whether the application is in the process of exiting.
    /// This is used to coordinate shutdown logic, especially for the "hide to tray" feature.
    /// </summary>
    public static bool IsExiting { get; set; }

    static App() {
        Services = ConfigureServices();
        InitializeDatabase();
    }

    public App() {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
    }

    public static Window? RootWindow { get; private set; }
    public static IServiceProvider Services { get; }
    public static App? CurrentApp { get; private set; }
    public static DispatcherQueue? MainDispatcherQueue { get; private set; }

    private static IServiceProvider ConfigureServices() {
        var services = new ServiceCollection();
        services.AddSingleton<IAudioPlayer>(provider => {
            if (MainDispatcherQueue == null)
                throw new InvalidOperationException("MainDispatcherQueue must be initialized before creating the AudioPlayerService.");
            return new AudioPlayerService(MainDispatcherQueue);
        });
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddSingleton<IMetadataExtractor, TagLibMetadataExtractor>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddTransient<MusicDbContext>();
        services.AddSingleton<PlayerViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<PlaylistViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<TrayIconViewModel>();
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

    protected override async void OnLaunched(LaunchActivatedEventArgs args) {
        _window = new MainWindow();
        RootWindow = _window;
        MainDispatcherQueue = _window.DispatcherQueue;

        TrySetMicaBackdrop();

        // The Window.Closed event is the definitive trigger for application cleanup.
        _window.Closed += async (sender, e) => {
            await HandleAppShutdownAsync();
        };

        var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
        var audioPlayerService = Services.GetRequiredService<IAudioPlayer>();

        try {
            await playbackService.InitializeAsync();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[App] CRITICAL: Failed to initialize MusicPlaybackService. {ex.Message}");
        }

        ReapplyCurrentDynamicTheme();
        await CheckAndNavigateToMainContent();
        _window.Activate();

        MainDispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => {
            try {
                audioPlayerService.InitializeSmtc();
            }
            catch (Exception ex) {
                Debug.WriteLine($"[App] ERROR: Failed to initialize SMTC. {ex.Message}");
            }
        });
    }

    private async Task HandleAppShutdownAsync() {
        Debug.WriteLine("[App] Starting application shutdown cleanup...");

        await SaveApplicationStateAsync();

        _micaController?.Dispose();
        _micaController = null;

        if (Services.GetService<TrayIconViewModel>() is IDisposable disposableTrayViewModel) {
            disposableTrayViewModel.Dispose();
        }

        Debug.WriteLine("[App] Application shutdown cleanup finished.");
    }

    private async Task SaveApplicationStateAsync() {
        var musicPlaybackService = Services.GetService<IMusicPlaybackService>();
        if (musicPlaybackService != null)
            try {
                await musicPlaybackService.SavePlaybackStateAsync();
            }
            catch (Exception ex) {
                Debug.WriteLine($"[App] ERROR: Failed to save playback state. {ex.Message}");
            }
    }

    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        Debug.WriteLine($"[App] UNHANDLED EXCEPTION: {e.Message}\n{e.Exception}");
        // Setting e.Handled to true can prevent the application from crashing,
        // but it should be used with caution as it may leave the app in an unstable state.
    }



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

    public void ApplyTheme(ElementTheme themeToApply) {
        if (RootWindow?.Content is FrameworkElement rootElement) {
            rootElement.RequestedTheme = themeToApply;
            ReapplyCurrentDynamicTheme();

            if (RootWindow is MainWindow mainWindow) mainWindow.InitializeCustomTitleBar();
        }
    }

    public void SetAppPrimaryColorBrushColor(Color newColor) {
        if (Resources.TryGetValue("AppPrimaryColorBrush", out var brushObject) &&
            brushObject is SolidColorBrush appPrimaryColorBrush) {
            if (appPrimaryColorBrush.Color != newColor) appPrimaryColorBrush.Color = newColor;
        }
        else {
            Debug.WriteLine("[App] CRITICAL: AppPrimaryColorBrush resource not found or is not a SolidColorBrush.");
        }
    }

    public void ActivateDefaultPrimaryColor() {
        SetAppPrimaryColorBrushColor(DefaultAccentColor);
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

        var currentTheme = rootElement.ActualTheme;
        var swatchToUse = currentTheme == ElementTheme.Dark ? darkSwatchId : lightSwatchId;

        if (!string.IsNullOrEmpty(swatchToUse) && TryParseHexColor(swatchToUse, out var targetColor))
            SetAppPrimaryColorBrushColor(targetColor);
        else
            ActivateDefaultPrimaryColor();
    }

    public void ReapplyCurrentDynamicTheme() {
        var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
        if (playbackService.CurrentTrack != null) {
            ApplyDynamicThemeFromSwatches(playbackService.CurrentTrack.LightSwatchId,
                playbackService.CurrentTrack.DarkSwatchId);
        }
        else {
            ActivateDefaultPrimaryColor();
        }
    }

    private bool TryParseHexColor(string hex, out Color color) {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (uint.TryParse(hex, NumberStyles.HexNumber, null, out var argb)) {
            if (hex.Length == 6) {
                color = Color.FromArgb(255, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF),
                    (byte)(argb & 0xFF));
                return true;
            }

            if (hex.Length == 8) {
                color = Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
                    (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            }
        }

        return false;
    }

    private bool TrySetMicaBackdrop() {
        if (MicaController.IsSupported()) {
            _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
            _wsdqHelper.EnsureWindowsSystemDispatcherQueueController();

            var configurationSource = new SystemBackdropConfiguration();
            configurationSource.IsInputActive = true;

            if (RootWindow?.Content is FrameworkElement rootElement) {
                rootElement.ActualThemeChanged += (s, e) => {
                    if (configurationSource != null) {
                        configurationSource.Theme = rootElement.ActualTheme switch {
                            ElementTheme.Dark => SystemBackdropTheme.Dark,
                            ElementTheme.Light => SystemBackdropTheme.Light,
                            _ => SystemBackdropTheme.Default
                        };
                    }
                };
                configurationSource.Theme = rootElement.ActualTheme switch {
                    ElementTheme.Dark => SystemBackdropTheme.Dark,
                    ElementTheme.Light => SystemBackdropTheme.Light,
                    _ => SystemBackdropTheme.Default
                };
            }

            _micaController = new MicaController();
            _micaController.SetSystemBackdropConfiguration(configurationSource);
            if (RootWindow != null) {
                _micaController.AddSystemBackdropTarget(RootWindow.As<ICompositionSupportsSystemBackdrop>());
                return true;
            }
        }
        return false;
    }
}