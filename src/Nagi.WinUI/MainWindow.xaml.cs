using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.UI;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;
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

    private AppWindow? _appWindow;
    private SizeInt32 _lastWindowSize;
    private PointInt32 _lastWindowPosition;

    private bool _isBackdropInitialized;
    private bool _isTitleBarInitialized;
    private ILogger<MainWindow>? _logger;
    private FrameworkElement? _rootElement;
    private IUISettingsService? _settingsService;
    private ITaskbarService? _taskbarService;
    private TransparencyEffectsMonitor? _transparencyEffects;

    internal ITaskbarService? TaskbarService => _taskbarService;

    private bool _isTaskbarInitialized;
    private int _isCleanedUp;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
    }

    public void InitializeDependencies(IUISettingsService settingsService, ITaskbarService taskbarService,
        TransparencyEffectsMonitor transparencyEffects, ILogger<MainWindow> logger)
    {
        _settingsService = settingsService;
        _taskbarService = taskbarService;
        _transparencyEffects = transparencyEffects;
        _logger = logger;
        _appWindow = AppWindow;
        _lastWindowSize = _appWindow.Size;
        _lastWindowPosition = _appWindow.Position;
        _appWindow.Changed += OnWindowGeometryChanged;
        Activated += OnWindowActivated;
        _settingsService.BackdropMaterialChanged += OnBackdropMaterialChanged;
        _transparencyEffects.Changed += OnTransparencyEffectsChanged;
    }

    public void Cleanup()
    {
        if (Interlocked.Exchange(ref _isCleanedUp, 1) != 0) return;

        Activated -= OnWindowActivated;
        if (_appWindow != null) _appWindow.Changed -= OnWindowGeometryChanged;

        if (_rootElement != null) _rootElement.ActualThemeChanged -= OnActualThemeChanged;
        if (_settingsService != null)
        {
            _settingsService.BackdropMaterialChanged -= OnBackdropMaterialChanged;
        }

        if (_transparencyEffects != null) _transparencyEffects.Changed -= OnTransparencyEffectsChanged;

        // Restore original WndProc before window destruction to prevent crashes
        // from callbacks to a deallocated delegate.
        if (_oldWndProc != 0)
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            TaskbarNativeMethods.SetWindowLongPtr(windowHandle, TaskbarNativeMethods.GWLP_WNDPROC, _oldWndProc);
            _oldWndProc = 0;
            _wndProcDelegate = null;
        }

        _taskbarService?.Dispose();

        _appWindow = null;
    }

    public void NotifyContentLoaded()
    {
        if (_rootElement != null) _rootElement.ActualThemeChanged -= OnActualThemeChanged;
        _rootElement = Content as FrameworkElement;
        if (_rootElement != null) _rootElement.ActualThemeChanged += OnActualThemeChanged;
        UpdateTitleBarTheme();
    }

    public void InitializeCustomTitleBar()
    {
        _appWindow ??= AppWindow;

        if (_appWindow.Presenter is not OverlappedPresenter presenter)
        {
            RevertToDefaultTitleBar();
            return;
        }

        if (Content is ICustomTitleBarProvider provider && provider.GetAppTitleBarElement() is { } titleBarElement)
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(titleBarElement);
            presenter.SetBorderAndTitleBar(true, Content is not SplashPage);
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

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_isTitleBarInitialized)
        {
            InitializeCustomTitleBar();
            _isTitleBarInitialized = true;
        }

        if (!_isBackdropInitialized)
        {
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
    }

    /// <summary>
    ///     Saves the current window size and/or position if the respective settings are enabled.
    ///     This should be called explicitly by the application before shutdown.
    /// </summary>
    public async Task SaveWindowStateAsync()
    {
        if (_settingsService == null) return;

        if (await _settingsService.GetRememberWindowSizeEnabledAsync())
        {
            var size = _lastWindowSize;
            await _settingsService.SetLastWindowSizeAsync(size.Width, size.Height);
            _logger?.LogDebug("Saving window size: {Width}x{Height}", size.Width, size.Height);
        }

        if (await _settingsService.GetRememberWindowPositionEnabledAsync())
        {
            var position = _lastWindowPosition;
            await _settingsService.SetLastWindowPositionAsync(position.X, position.Y);
            _logger?.LogDebug("Saving window position: ({X}, {Y})", position.X, position.Y);
        }
    }

    private void OnWindowGeometryChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;

        if (args.DidSizeChange) _lastWindowSize = sender.Size;
        if (args.DidPositionChange) _lastWindowPosition = sender.Position;
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

    private async Task TrySetBackdropAsync(BackdropMaterial? material = null)
    {
        if (_settingsService is null || _transparencyEffects is null) return;

        try
        {
            // Use the high-level XAML MicaBackdrop and DesktopAcrylicBackdrop objects.
            // The framework handles the controllers and configuration automatically.
            if (_transparencyEffects.IsEnabled)
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

        _appWindow ??= AppWindow;

        try
        {
            var savedSize = await _settingsService.GetRememberWindowSizeEnabledAsync()
                ? await _settingsService.GetLastWindowSizeAsync()
                : null;
            var savedPosition = await _settingsService.GetRememberWindowPositionEnabledAsync()
                ? await _settingsService.GetLastWindowPositionAsync()
                : null;

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

    private const int HorizontalScreenMargin = 10;
    private const int VerticalScreenMargin = 48; // Larger to account for taskbar height.

    private readonly AppWindow _appWindow;
    private readonly ILogger<MiniPlayerWindow> _logger;
    private readonly MiniPlayerView _view;
    private readonly IWin32InteropService _win32InteropService;

    public MiniPlayerWindow(PlayerViewModel playerViewModel, IThemeService themeService,
        IWin32InteropService win32InteropService, ILogger<MiniPlayerWindow> logger)
    {
        _logger = logger;
        _win32InteropService = win32InteropService;
        _view = new MiniPlayerView(this, playerViewModel);
        Content = _view;
        _appWindow = AppWindow;

        SystemBackdrop = new DesktopAcrylicBackdrop();

        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = themeService.CurrentTheme.ToElementTheme();
        }

        ConfigureAppWindow();
        SubscribeToEvents();
    }

    private void ConfigureAppWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_view.TitleBarDragRegion);

        _appWindow.Title = Strings.MiniPlayer_Title;
        _appWindow.SetIcon(AppIconPath);

        // Convert logical pixels to physical pixels for AppWindow.Resize()
        var initialSize = (int)(InitialWindowSizeDips * GetDpiScale());
        _appWindow.Resize(new SizeInt32(initialSize, initialSize));
        PositionWindowInBottomRight(_appWindow, initialSize);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false); // Keep a border but hide the system title text.
        }
        else
        {
            _logger.LogWarning("Could not configure presenter. It is not an OverlappedPresenter");
        }
    }

    private void PositionWindowInBottomRight(AppWindow appWindow, int windowSize)
    {
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

    private void SubscribeToEvents()
    {
        _view.RestoreButtonClicked += OnRestoreButtonClicked;
        _appWindow.Changed += OnAppWindowChanged;
        Closed += OnWindowClosed;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
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
    ///     Computed on demand because the DPI changes when the window moves between monitors.
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
        // Closing during the click event can crash WinUI with E_UNEXPECTED;
        // a dispatcher enqueue alone does not avoid it, so defer closure briefly.
        _appWindow.Hide();

        Task.Delay(50).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() => Close());
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _view.RestoreButtonClicked -= OnRestoreButtonClicked;
        _appWindow.Changed -= OnAppWindowChanged;
    }
}
