using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
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
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;
using WinRT.Interop;

namespace Nagi.WinUI;

/// <summary>
///     The main application window. It manages the window frame, hosts application content,
///     dynamically configures a custom or default title bar, and manages the backdrop material.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Minimum window size constraints when restoring saved dimensions.
    private const int MinMainWindowWidth = 400;
    private const int MinMainWindowHeight = 300;
    
    // Core windowing and service references.
    private AppWindow? _appWindow;

    // State flags to ensure one-time initialization.
    private bool _isBackdropInitialized;
    private bool _isTitleBarInitialized;
    private ILogger<MainWindow>? _logger;
    private FrameworkElement? _rootElement;
    private IUISettingsService? _settingsService;
    private ITaskbarService? _taskbarService;

    internal ITaskbarService? TaskbarService => _taskbarService;

    private bool _isTaskbarInitialized;
    private int _isCleanedUp;

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
        _taskbarService = App.Services!.GetRequiredService<ITaskbarService>();
        Activated += OnWindowActivated;
        _settingsService.BackdropMaterialChanged += OnBackdropMaterialChanged;
        _settingsService.TransparencyEffectsSettingChanged += OnTransparencyEffectsChanged;
    }

    public void Cleanup()
    {
        // Ensure cleanup only runs once to prevent race conditions
        if (Interlocked.Exchange(ref _isCleanedUp, 1) != 0) return;

        Activated -= OnWindowActivated;

        if (_rootElement != null) _rootElement.ActualThemeChanged -= OnActualThemeChanged;
        if (_settingsService != null)
        {
            _settingsService.BackdropMaterialChanged -= OnBackdropMaterialChanged;
            _settingsService.TransparencyEffectsSettingChanged -= OnTransparencyEffectsChanged;
        }

        // Restore original WndProc before window destruction to prevent crashes
        // from callbacks to a deallocated delegate.
        if (_oldWndProc != 0)
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            TaskbarNativeMethods.SetWindowLongPtr(windowHandle, TaskbarNativeMethods.GWLP_WNDPROC, _oldWndProc);
            _oldWndProc = 0;
            _wndProcDelegate = null;
        }

        // Dispose the taskbar service to release COM objects and icons.
        _taskbarService?.Dispose();

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
        presenter?.SetBorderAndTitleBar(true, true);
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

        if (!_isTaskbarInitialized && _taskbarService != null)
        {
            _isTaskbarInitialized = true;
            var windowHandle = WindowNative.GetWindowHandle(this);
            _taskbarService.Initialize(windowHandle);
            InitializeWndProc();
        }

        if (Content is MainPage mainPage) mainPage.UpdateActivationVisualState(args.WindowActivationState);
    }

    /// <summary>
    ///     Saves the current window size and/or position if the respective settings are enabled.
    ///     This should be called explicitly by the application before shutdown.
    /// </summary>
    public async Task SaveWindowStateAsync()
    {
        if (_settingsService == null) return;

        // Ensure _appWindow is available. It might not be set if the window was never
        // activated (e.g., when starting minimized or hidden to tray).
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null)
        {
            _logger?.LogDebug("Cannot save window state: AppWindow not available");
            return;
        }

        try
        {
            var saveSizeTask = _settingsService.GetRememberWindowSizeEnabledAsync();
            var savePositionTask = _settingsService.GetRememberWindowPositionEnabledAsync();
            
            await Task.WhenAll(saveSizeTask, savePositionTask);

            var saveSize = saveSizeTask.Result;
            var savePosition = savePositionTask.Result;
            
            // Parallelize the actual save operations when both are enabled
            var saveTasks = new List<Task>();
            
            if (saveSize)
            {
                var size = _appWindow.Size;
                saveTasks.Add(_settingsService.SetLastWindowSizeAsync(size.Width, size.Height));
                _logger?.LogDebug("Saving window size: {Width}x{Height}", size.Width, size.Height);
            }
            
            if (savePosition)
            {
                var position = _appWindow.Position;
                saveTasks.Add(_settingsService.SetLastWindowPositionAsync(position.X, position.Y));
                _logger?.LogDebug("Saving window position: ({X}, {Y})", position.X, position.Y);
            }
            
            if (saveTasks.Count > 0)
            {
                await Task.WhenAll(saveTasks);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save window state.");
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

        try
        {
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set system backdrop.");
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
    ///     Restores the main window size and/or position from saved settings if the features are enabled.
    ///     This should be called before the window is shown to prevent visual flashes.
    /// </summary>
    public async Task RestoreWindowStateAsync()
    {
        if (_settingsService == null) return;

        // Ensure _appWindow is available.
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null)
        {
            _logger?.LogDebug("Cannot restore window state: AppWindow not available");
            return;
        }

        try
        {
            var restoreSizeTask = _settingsService.GetRememberWindowSizeEnabledAsync();
            var restorePositionTask = _settingsService.GetRememberWindowPositionEnabledAsync();
            
            await Task.WhenAll(restoreSizeTask, restorePositionTask);

            var restoreSize = restoreSizeTask.Result;
            var restorePosition = restorePositionTask.Result;
            
            // Parallelize fetching saved values when both are enabled
            var savedSizeTask = restoreSize 
                ? _settingsService.GetLastWindowSizeAsync() 
                : Task.FromResult<(int Width, int Height)?>(null);
            var savedPositionTask = restorePosition 
                ? _settingsService.GetLastWindowPositionAsync() 
                : Task.FromResult<(int X, int Y)?>(null);
            
            await Task.WhenAll(savedSizeTask, savedPositionTask);
            
            var savedSize = savedSizeTask.Result;
            var savedPosition = savedPositionTask.Result;

            // Apply size and/or position using the most efficient method
            if (savedSize.HasValue && savedPosition.HasValue)
            {
                var width = Math.Max(savedSize.Value.Width, MinMainWindowWidth);
                var height = Math.Max(savedSize.Value.Height, MinMainWindowHeight);
                
                _appWindow.MoveAndResize(new RectInt32(
                    savedPosition.Value.X, savedPosition.Value.Y,
                    width, height));
                    
                _logger?.LogDebug("Restored window state: ({X}, {Y}) {Width}x{Height}", 
                    savedPosition.Value.X, savedPosition.Value.Y, width, height);
            }
            else if (savedSize.HasValue)
            {
                var width = Math.Max(savedSize.Value.Width, MinMainWindowWidth);
                var height = Math.Max(savedSize.Value.Height, MinMainWindowHeight);
                
                _appWindow.Resize(new SizeInt32(width, height));
                _logger?.LogDebug("Restored window size: {Width}x{Height}", width, height);
            }
            else if (savedPosition.HasValue)
            {
                _appWindow.Move(new PointInt32(savedPosition.Value.X, savedPosition.Value.Y));
                _logger?.LogDebug("Restored window position: ({X}, {Y})", savedPosition.Value.X, savedPosition.Value.Y);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to restore window state.");
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

    #region WndProc

    private TaskbarNativeMethods.WindowProc? _wndProcDelegate;
    private nint _oldWndProc;

    private void InitializeWndProc()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        _wndProcDelegate = WndProc;
        var wndProcPtr = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _oldWndProc = TaskbarNativeMethods.SetWindowLongPtr(windowHandle, TaskbarNativeMethods.GWLP_WNDPROC, wndProcPtr);
    }

    private nint WndProc(nint hWnd, int msg, nint wParam, nint lParam)
    {
        _taskbarService?.HandleWindowMessage(msg, wParam, lParam);
        return TaskbarNativeMethods.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    #endregion
}

/// <summary>
///     A secondary window that serves as an always-on-top, resizable mini-player.
///     It maintains a square aspect ratio and positions itself in the corner of the screen.
/// </summary>
public sealed class MiniPlayerWindow : Window
{
    // Constants for window appearance and behavior (in DIPs - device-independent pixels).
    private const int InitialWindowSizeDips = 350;
    private const int MinWindowSizeDips = 200;
    private const int MaxWindowSizeDips = 640;
    private const float BaseDpi = 96.0f;
    private static readonly string AppIconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets/AppLogo.ico");

    // Margins to position the window away from the screen edges.
    private const int HorizontalScreenMargin = 10;
    private const int VerticalScreenMargin = 48; // Larger to account for taskbar height.

    private readonly AppWindow _appWindow;
    private readonly ILogger<MiniPlayerWindow> _logger;
    private readonly MiniPlayerView _view;
    private readonly IWin32InteropService _win32InteropService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MiniPlayerWindow" /> class.
    /// </summary>
    public MiniPlayerWindow()
    {
        var services = App.Services!;
        _logger = services.GetRequiredService<ILogger<MiniPlayerWindow>>();
        _win32InteropService = services.GetRequiredService<IWin32InteropService>();
        _view = new MiniPlayerView(this);
        Content = _view;
        _appWindow = AppWindow;

        // Apply acrylic backdrop for a premium look
        SystemBackdrop = new DesktopAcrylicBackdrop();

        // Ensure the mini player follows the application theme
        var themeService = services.GetRequiredService<IThemeService>();
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = themeService.CurrentTheme;
        }

        ConfigureAppWindow();
        SubscribeToEvents();
    }

    /// <summary>
    ///     Configures the properties of the AppWindow for the mini-player.
    /// </summary>
    private void ConfigureAppWindow()
    {
        ExtendsContentIntoTitleBar = true; // Allows content to draw into the title bar area.

        _appWindow.Title = Nagi.WinUI.Resources.Strings.MiniPlayer_Title;
        _appWindow.SetIcon(AppIconPath);
        
        // Convert logical pixels to physical pixels for AppWindow.Resize()
        var initialSize = (int)(InitialWindowSizeDips * GetDpiScale());
        _appWindow.Resize(new SizeInt32(initialSize, initialSize));
        PositionWindowInBottomRight(_appWindow, initialSize);

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
    /// <param name="appWindow">The app window to position.</param>
    /// <param name="windowSize">The physical size of the window in pixels.</param>
    private void PositionWindowInBottomRight(AppWindow appWindow, int windowSize)
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
        var positionX = workArea.X + workArea.Width - windowSize - HorizontalScreenMargin;
        var positionY = workArea.Y + workArea.Height - windowSize - VerticalScreenMargin;
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
    ///     clamping the size between defined minimum and maximum values (in physical pixels).
    /// </summary>
    private void MaintainSquareAspectRatio(AppWindow window)
    {
        var currentPosition = window.Position;
        var currentSize = window.Size;

        // Convert logical pixel constraints to physical pixels
        var dpiScale = GetDpiScale();
        var minSize = (int)(MinWindowSizeDips * dpiScale);
        var maxSize = (int)(MaxWindowSizeDips * dpiScale);

        var desiredSize = Math.Max(currentSize.Width, currentSize.Height);
        var newSize = Math.Clamp(desiredSize, minSize, maxSize);
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

    /// <summary>
    ///     Gets the DPI scale factor for this window.
    ///     Calculates on-demand to handle multi-monitor scenarios where DPI may change.
    /// </summary>
    private float GetDpiScale()
    {
        try
        {
            var windowHandle = WindowNative.GetWindowHandle(this);
            var dpi = _win32InteropService.GetDpiForWindow(windowHandle);
            return dpi / BaseDpi;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get DPI, using default scale of 1.0");
            return 1.0f;
        }
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