using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
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

/// <summary>
///     Provides application-specific behavior to supplement the default Application class.
///     This class is the main entry point for the application.
/// </summary>
public partial class App : Application
{
    private static readonly Color DefaultAccentColor = Color.FromArgb(255, 96, 198, 137);
    private MicaController? _micaController;

    private Window? _window;
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

    static App()
    {
        Services = ConfigureServices();
        InitializeDatabase();
    }

    public App()
    {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        CoreApplication.Suspending += OnSuspending;
    }

    public static Window? RootWindow { get; private set; }
    public static IServiceProvider Services { get; }
    public static App? CurrentApp { get; private set; }
    public static DispatcherQueue? MainDispatcherQueue { get; private set; }

    /// <summary>
    ///     Configures the dependency injection container for the application.
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAudioPlayer>(provider =>
        {
            if (MainDispatcherQueue == null)
                throw new InvalidOperationException(
                    "MainDispatcherQueue must be initialized before creating the AudioPlayerService.");
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
    ///     Ensures the application's database is created on startup.
    /// </summary>
    private static void InitializeDatabase()
    {
        try
        {
            using var dbContext = Services.GetRequiredService<MusicDbContext>();
            dbContext.Database.EnsureCreated();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] CRITICAL: Failed to initialize database. {ex.Message}");
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        RootWindow = _window;
        MainDispatcherQueue = _window.DispatcherQueue;

        TrySetMicaBackdrop();

        _window.Closed += async (sender, e) =>
        {
            await SaveApplicationStateAsync();

            if (_micaController != null)
            {
                _micaController.Dispose();
                _micaController = null;
            }
        };

        var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
        var audioPlayerService = Services.GetRequiredService<IAudioPlayer>();

        try
        {
            await playbackService.InitializeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] CRITICAL: Failed to initialize MusicPlaybackService. {ex.Message}");
        }

        ReapplyCurrentDynamicTheme();
        await CheckAndNavigateToMainContent();
        _window.Activate();

        MainDispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                audioPlayerService.InitializeSmtc();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] ERROR: Failed to initialize SMTC. {ex.Message}");
            }
        });
    }

    private async void OnSuspending(object? sender, SuspendingEventArgs e)
    {
        var deferral = e.SuspendingOperation.GetDeferral();
        await SaveApplicationStateAsync();
        deferral.Complete();
    }

    private async Task SaveApplicationStateAsync()
    {
        var musicPlaybackService = Services.GetService<IMusicPlaybackService>();
        if (musicPlaybackService != null)
            try
            {
                await musicPlaybackService.SavePlaybackStateAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] ERROR: Failed to save playback state. {ex.Message}");
            }
    }

    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"[App] UNHANDLED EXCEPTION: {e.Message}\n{e.Exception}");
        e.Handled = true;
    }

    /// <summary>
    ///     Checks if a music library has been configured and navigates to the appropriate initial page.
    /// </summary>
    public async Task CheckAndNavigateToMainContent()
    {
        if (RootWindow == null) return;

        var libraryService = Services.GetRequiredService<ILibraryService>();
        var hasFolders = (await libraryService.GetAllFoldersAsync()).Any();

        if (hasFolders)
        {
            if (RootWindow.Content is not MainPage) RootWindow.Content = new MainPage();
            var libraryViewModel = Services.GetRequiredService<LibraryViewModel>();
            await libraryViewModel.InitializeAndStartBackgroundScanAsync();
        }
        else
        {
            if (RootWindow.Content is not OnboardingPage) RootWindow.Content = new OnboardingPage();
        }

        if (RootWindow.Content is FrameworkElement currentContent && RootWindow is MainWindow mainWindow)
        {
            var settingsService = Services.GetRequiredService<ISettingsService>();
            currentContent.RequestedTheme = await settingsService.GetThemeAsync();
            mainWindow.InitializeCustomTitleBar();
        }
    }

    /// <summary>
    ///     Applies the specified theme to the application's root element.
    /// </summary>
    public void ApplyTheme(ElementTheme themeToApply)
    {
        if (RootWindow?.Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = themeToApply;
            ReapplyCurrentDynamicTheme();

            if (RootWindow is MainWindow mainWindow) mainWindow.InitializeCustomTitleBar();
        }
    }

    /// <summary>
    ///     Sets the color of the application's primary accent color brush.
    /// </summary>
    public void SetAppPrimaryColorBrushColor(Color newColor)
    {
        if (Resources.TryGetValue("AppPrimaryColorBrush", out var brushObject) &&
            brushObject is SolidColorBrush appPrimaryColorBrush)
        {
            if (appPrimaryColorBrush.Color != newColor) appPrimaryColorBrush.Color = newColor;
        }
        else
        {
            Debug.WriteLine("[App] CRITICAL: AppPrimaryColorBrush resource not found or is not a SolidColorBrush.");
        }
    }

    /// <summary>
    ///     Resets the application's primary accent color to its default value.
    /// </summary>
    public void ActivateDefaultPrimaryColor()
    {
        SetAppPrimaryColorBrushColor(DefaultAccentColor);
    }

    /// <summary>
    ///     Applies a dynamic primary color based on color swatches extracted from album art.
    /// </summary>
    /// <param name="lightSwatchId">The hex color string for the light theme.</param>
    /// <param name="darkSwatchId">The hex color string for the dark theme.</param>
    public async void ApplyDynamicThemeFromSwatches(string? lightSwatchId, string? darkSwatchId)
    {
        var settingsService = Services.GetRequiredService<ISettingsService>();
        if (!await settingsService.GetDynamicThemingAsync())
        {
            ActivateDefaultPrimaryColor();
            return;
        }

        if (RootWindow?.Content is not FrameworkElement rootElement)
        {
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

    /// <summary>
    ///     Re-evaluates and applies the dynamic theme for the currently playing track.
    /// </summary>
    public void ReapplyCurrentDynamicTheme()
    {
        var playbackService = Services.GetRequiredService<IMusicPlaybackService>();
        ApplyDynamicThemeFromSwatches(playbackService.CurrentTrack?.LightSwatchId,
            playbackService.CurrentTrack?.DarkSwatchId);
    }


    private bool TryParseHexColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (uint.TryParse(hex, NumberStyles.HexNumber, null, out var argb))
        {
            if (hex.Length == 6)
            {
                color = Color.FromArgb(255, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF),
                    (byte)(argb & 0xFF));
                return true;
            }

            if (hex.Length == 8)
            {
                color = Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
                    (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Attempts to apply the Mica backdrop to the main window if supported by the system.
    /// </summary>
    private bool TrySetMicaBackdrop()
    {
        if (MicaController.IsSupported())
        {
            _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
            _wsdqHelper.EnsureWindowsSystemDispatcherQueueController();

            var configurationSource = new SystemBackdropConfiguration();

            if (RootWindow?.Content is FrameworkElement rootElement)
                rootElement.ActualThemeChanged += (s, e) =>
                {
                    configurationSource.Theme = rootElement.ActualTheme switch
                    {
                        ElementTheme.Dark => SystemBackdropTheme.Dark,
                        ElementTheme.Light => SystemBackdropTheme.Light,
                        _ => SystemBackdropTheme.Default
                    };
                };

            _micaController = new MicaController();
            _micaController.SetSystemBackdropConfiguration(configurationSource);
            _micaController.AddSystemBackdropTarget(RootWindow.As<ICompositionSupportsSystemBackdrop>());

            return true;
        }

        return false;
    }
}