using System;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Models;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using WinRT.Interop;

namespace Nagi.WinUI;

/// <summary>
///     The main application window. It manages the window frame, hosts application content,
///     dynamically configures a custom or default title bar, and manages the backdrop material.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Core windowing and service references.
    private AppWindow? _appWindow;

    // State flags to ensure one-time initialization.
    private bool _isBackdropInitialized;
    private bool _isTitleBarInitialized;
    private bool _isWindowSizeRestored;
    private ILogger<MainWindow>? _logger;
    private FrameworkElement? _rootElement;
    private IUISettingsService? _settingsService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MainWindow" /> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
    }

    /// <summary>
    ///     Initializes the window with required services and subscribes to necessary events.
    /// </summary>
    public void InitializeDependencies(IUISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = App.Services!.GetRequiredService<ILogger<MainWindow>>();
        Activated += OnWindowActivated;
        _settingsService.BackdropMaterialChanged += OnBackdropMaterialChanged;
        _settingsService.TransparencyEffectsSettingChanged += OnTransparencyEffectsChanged;
    }

    public void Cleanup()
    {
        Activated -= OnWindowActivated;
        
        if (_rootElement != null) _rootElement.ActualThemeChanged -= OnActualThemeChanged;
        if (_settingsService != null)
        {
            _settingsService.BackdropMaterialChanged -= OnBackdropMaterialChanged;
            _settingsService.TransparencyEffectsSettingChanged -= OnTransparencyEffectsChanged;
        }

        _appWindow = null;
    }

    /// <summary>
    ///     Notifies the window that its content has been loaded, allowing synchronization of UI themes.
    /// </summary>
    public void NotifyContentLoaded()
    {
        if (_rootElement != null) _rootElement.ActualThemeChanged -= OnActualThemeChanged;
        _rootElement = Content as FrameworkElement;
        if (_rootElement != null) _rootElement.ActualThemeChanged += OnActualThemeChanged;
        UpdateTitleBarTheme();
    }

    /// <summary>
    ///     Configures the window's title bar based on the current page's content.
    /// </summary>
    public void InitializeCustomTitleBar()
    {
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null)
        {
            _logger?.LogCritical("AppWindow is not available. Cannot initialize title bar");
            return;
        }

        if (_appWindow.Presenter is not OverlappedPresenter presenter)
        {
            RevertToDefaultTitleBar();
            return;
        }

        if (Content is ICustomTitleBarProvider provider && provider.GetAppTitleBarElement() is { } titleBarElement)
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(titleBarElement);
            var showSystemButtons = Content is not OnboardingPage;
            presenter.SetBorderAndTitleBar(true, showSystemButtons);
        }
        else
        {
            RevertToDefaultTitleBar(presenter);
        }

        UpdateTitleBarTheme();
    }

    private void RevertToDefaultTitleBar(OverlappedPresenter? presenter = null)
    {
        ExtendsContentIntoTitleBar = false;
        SetTitleBar(null);
        presenter ??= _appWindow?.Presenter as OverlappedPresenter;
        presenter?.SetBorderAndTitleBar(false, true);
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_isTitleBarInitialized)
        {
            InitializeCustomTitleBar();
            _isTitleBarInitialized = true;
        }

        if (!_isBackdropInitialized)
        {
            // The async void is acceptable here as this is a top-level event handler.
            // Any exceptions are handled within TrySetBackdropAsync.
            _ = TrySetBackdropAsync();
            _isBackdropInitialized = true;
        }

        if (!_isWindowSizeRestored && _settingsService != null)
        {
            _isWindowSizeRestored = true;
            await RestoreWindowSizeAsync();
        }

        if (Content is MainPage mainPage) mainPage.UpdateActivationVisualState(args.WindowActivationState);
    }

    /// <summary>
    ///     Saves the current window size if the setting is enabled.
    ///     This should be called explicitly by the application before shutdown.
    /// </summary>
    public async Task SaveWindowSizeAsync()
    {
        if (_settingsService != null && _appWindow != null)
        {
            try
            {
                if (await _settingsService.GetRememberWindowSizeEnabledAsync())
                {
                    var size = _appWindow.Size;
                    await _settingsService.SetLastWindowSizeAsync(size.Width, size.Height);
                    _logger?.LogDebug("Saved window size: {Width}x{Height}", size.Width, size.Height);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to save window size.");
            }
        }
    }

    private void OnBackdropMaterialChanged(BackdropMaterial material)
    {
        _ = TrySetBackdropAsync(material);
    }

    private void OnTransparencyEffectsChanged(bool isEnabled)
    {
        _ = TrySetBackdropAsync();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTitleBarTheme();
    }

    /// <summary>
    ///     Attempts to set the system backdrop based on user settings. This method is now much simpler.
    /// </summary>
    private async Task TrySetBackdropAsync(BackdropMaterial? material = null)
    {
        if (_settingsService is null) return;

        // Use the high-level XAML MicaBackdrop and DesktopAcrylicBackdrop objects.
        // The framework handles the controllers and configuration automatically.
        if (_settingsService.IsTransparencyEffectsEnabled())
        {
            material ??= await _settingsService.GetBackdropMaterialAsync();

            switch (material)
            {
                case BackdropMaterial.Mica:
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    break;
                case BackdropMaterial.MicaAlt:
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                    break;
                case BackdropMaterial.Acrylic:
                    SystemBackdrop = new DesktopAcrylicBackdrop();
                    break;
                default:
                    SystemBackdrop = null;
                    break;
            }
        }
        else
        {
            SystemBackdrop = null;
        }
    }

    private void UpdateTitleBarTheme()
    {
        if (_appWindow?.TitleBar is not { } titleBar || _rootElement is null) return;

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (_rootElement.ActualTheme == ElementTheme.Dark)
        {
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x99, 0x99, 0x99);
        }
        else
        {
            titleBar.ButtonForegroundColor = Colors.Black;
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x20, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = Colors.Black;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x40, 0x00, 0x00, 0x00);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x66, 0x66, 0x66);
        }
    }

    /// <summary>
    ///     Restores the main window size from saved settings if the feature is enabled.
    /// </summary>
    private async Task RestoreWindowSizeAsync()
    {
        if (_settingsService == null || _appWindow == null) return;

        try
        {
            if (await _settingsService.GetRememberWindowSizeEnabledAsync())
            {
                var savedSize = await _settingsService.GetLastWindowSizeAsync();
                if (savedSize.HasValue)
                {
                    // Ensure minimum size constraints
                    var width = Math.Max(savedSize.Value.Width, 400);
                    var height = Math.Max(savedSize.Value.Height, 300);
                    _appWindow.Resize(new SizeInt32(width, height));
                    _logger?.LogDebug("Restored window size: {Width}x{Height}", width, height);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to restore window size.");
        }
    }

    private AppWindow? GetAppWindowForCurrentWindow()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            if (hWnd == IntPtr.Zero) return null;
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(windowId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to retrieve AppWindow");
            return null;
        }
    }
}

/// <summary>
///     A secondary window that serves as an always-on-top, resizable mini-player.
///     It maintains a square aspect ratio and positions itself in the corner of the screen.
/// </summary>
public sealed class MiniPlayerWindow : Window
{
    // Constants for window appearance and behavior.
    private const int InitialWindowSize = 350;
    private const int MinWindowSize = 200;
    private const int MaxWindowSize = 640;
    private const string AppIconPath = "Assets/AppLogo.ico";

    // Margins to position the window away from the screen edges.
    private const int HorizontalScreenMargin = 10;
    private const int VerticalScreenMargin = 48; // Larger to account for taskbar height.

    private readonly AppWindow _appWindow;
    private readonly ILogger<MiniPlayerWindow> _logger;
    private readonly MiniPlayerView _view;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MiniPlayerWindow" /> class.
    /// </summary>
    public MiniPlayerWindow()
    {
        _logger = App.Services!.GetRequiredService<ILogger<MiniPlayerWindow>>();
        _view = new MiniPlayerView(this);
        Content = _view;
        _appWindow = AppWindow;

        ConfigureAppWindow();
        SubscribeToEvents();
    }

    /// <summary>
    ///     Configures the properties of the AppWindow for the mini-player.
    /// </summary>
    private void ConfigureAppWindow()
    {
        ExtendsContentIntoTitleBar = true; // Allows content to draw into the title bar area.

        _appWindow.Title = "Nagi";
        _appWindow.SetIcon(AppIconPath);
        _appWindow.Resize(new SizeInt32(InitialWindowSize, InitialWindowSize));
        PositionWindowInBottomRight(_appWindow);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false; // Disable maximize button.
            presenter.IsMinimizable = false; // Disable minimize button.
            presenter.SetBorderAndTitleBar(true, false); // Keep a border but hide the system title text.
        }
        else
        {
            _logger.LogWarning("Could not configure presenter. It is not an OverlappedPresenter");
        }
    }

    /// <summary>
    ///     Positions the window in the bottom-right corner of the primary display's work area.
    /// </summary>
    private void PositionWindowInBottomRight(AppWindow appWindow)
    {
        // Get the display area for the window, falling back to the primary display.
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea == null)
        {
            _logger.LogWarning("Could not retrieve display area to position the window");
            return;
        }

        // Use WorkArea to respect the user's taskbar position and avoid overlapping it.
        var workArea = displayArea.WorkArea;
        var positionX = workArea.X + workArea.Width - InitialWindowSize - HorizontalScreenMargin;
        var positionY = workArea.Y + workArea.Height - InitialWindowSize - VerticalScreenMargin;
        appWindow.Move(new PointInt32(positionX, positionY));
    }

    /// <summary>
    ///     Subscribes to necessary window and view events.
    /// </summary>
    private void SubscribeToEvents()
    {
        _view.RestoreButtonClicked += OnRestoreButtonClicked;
        _appWindow.Changed += OnAppWindowChanged;
        Closed += OnWindowClosed;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Enforce aspect ratio only when the size changes to avoid unnecessary calculations.
        if (args.DidSizeChange) MaintainSquareAspectRatio(sender);
    }

    /// <summary>
    ///     Enforces a square aspect ratio for the window during resizing,
    ///     clamping the size between defined minimum and maximum values.
    /// </summary>
    private void MaintainSquareAspectRatio(AppWindow window)
    {
        var currentPosition = window.Position;
        var currentSize = window.Size;

        var desiredSize = Math.Max(currentSize.Width, currentSize.Height);
        var newSize = Math.Clamp(desiredSize, MinWindowSize, MaxWindowSize);
        if (currentSize.Width == newSize && currentSize.Height == newSize) return;

        // To prevent the window from "jumping" during resize, calculate the new top-left
        // position required to keep the window's center point stationary.
        var centerX = currentPosition.X + currentSize.Width / 2;
        var centerY = currentPosition.Y + currentSize.Height / 2;
        var newX = centerX - newSize / 2;
        var newY = centerY - newSize / 2;

        // IMPORTANT: Temporarily unsubscribe to prevent recursive loop during resize
        window.Changed -= OnAppWindowChanged;
        window.MoveAndResize(new RectInt32(newX, newY, newSize, newSize));
        window.Changed += OnAppWindowChanged;
    }

    private void OnRestoreButtonClicked(object? sender, EventArgs e)
    {
        Close();
    }

    /// <summary>
    ///     Cleans up resources by unsubscribing from events when the window is closed.
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _view.RestoreButtonClicked -= OnRestoreButtonClicked;
        _appWindow.Changed -= OnAppWindowChanged;
    }
}