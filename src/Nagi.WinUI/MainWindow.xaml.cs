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
using Nagi.WinUI.Services.Implementations;
using WinRT.Interop;
using Nagi.Core.Settings;
using Nagi.Presentation.Services;
using Nagi.Core.Services.Abstractions;

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
    private ISettingsService? _settingsService;
    private TaskbarService? _taskbarService;

    internal TaskbarService? TaskbarService => _taskbarService;

    private bool _isTaskbarInitialized;
    private int _isCleanedUp;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
    }

    public void InitializeDependencies(ISettingsService settingsService, TaskbarService taskbarService,
        ILogger<MainWindow> logger)
    {
        _settingsService = settingsService;
        _taskbarService = taskbarService;
        _logger = logger;
        _appWindow = AppWindow;
        _lastWindowSize = _appWindow.Size;
        _lastWindowPosition = _appWindow.Position;
        _appWindow.Changed += OnWindowGeometryChanged;
        Activated += OnWindowActivated;
        _settingsService.SettingChanged += OnSettingChanged;
        SystemVisualSettings.TransparencyEffectsEnabledChanged += OnTransparencyEffectsChanged;
    }

    public void Cleanup()
    {
        if (Interlocked.Exchange(ref _isCleanedUp, 1) != 0) return;

        Activated -= OnWindowActivated;
        if (_appWindow != null) _appWindow.Changed -= OnWindowGeometryChanged;

        if (_rootElement != null) _rootElement.ActualThemeChanged -= OnActualThemeChanged;
        if (_settingsService != null) _settingsService.SettingChanged -= OnSettingChanged;

        SystemVisualSettings.TransparencyEffectsEnabledChanged -= OnTransparencyEffectsChanged;

        // Restore original WndProc before window destruction to prevent crashes
        // from callbacks to a deallocated delegate.
        if (_oldWndProc != 0)
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeMethods.SetWindowLongPtr(windowHandle, NativeMethods.GWLP_WNDPROC, _oldWndProc);
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
            TrySetBackdrop();
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
    public void SaveWindowState()
    {
        if (_settingsService == null) return;

        if (_settingsService.Get(AppSettings.RememberWindowSize))
        {
            var size = _lastWindowSize;
            _settingsService.Set(AppSettings.LastWindowSize, (size.Width, size.Height));
            _logger?.LogDebug("Saving window size: {Width}x{Height}", size.Width, size.Height);
        }

        if (_settingsService.Get(AppSettings.RememberWindowPosition))
        {
            var position = _lastWindowPosition;
            _settingsService.Set(AppSettings.LastWindowPosition, (position.X, position.Y));
            _logger?.LogDebug("Saving window position: ({X}, {Y})", position.X, position.Y);
        }
    }

    private void OnWindowGeometryChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;

        if (args.DidSizeChange) _lastWindowSize = sender.Size;
        if (args.DidPositionChange) _lastWindowPosition = sender.Position;
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting == AppSettings.BackdropMaterial) DispatcherQueue.TryEnqueue(TrySetBackdrop);
    }

    private void OnTransparencyEffectsChanged(bool isEnabled)
    {
        TrySetBackdrop();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTitleBarTheme();
    }

    private void TrySetBackdrop()
    {
        if (_settingsService is null) return;

        try
        {
            // Use the high-level XAML MicaBackdrop and DesktopAcrylicBackdrop objects.
            // The framework handles the controllers and configuration automatically.
            if (SystemVisualSettings.TransparencyEffectsEnabled)
            {
                switch (_settingsService.Get(AppSettings.BackdropMaterial))
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
    public void RestoreWindowState()
    {
        if (_settingsService == null) return;

        _appWindow ??= AppWindow;

        try
        {
            var savedSize = _settingsService.Get(AppSettings.RememberWindowSize)
                ? _settingsService.Get(AppSettings.LastWindowSize)
                : null;
            var savedPosition = _settingsService.Get(AppSettings.RememberWindowPosition)
                ? _settingsService.Get(AppSettings.LastWindowPosition)
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

    private NativeMethods.WindowProc? _wndProcDelegate;
    private nint _oldWndProc;

    private void InitializeWndProc()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        _wndProcDelegate = WndProc;
        var wndProcPtr = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _oldWndProc = NativeMethods.SetWindowLongPtr(windowHandle, NativeMethods.GWLP_WNDPROC, wndProcPtr);
    }

    private nint WndProc(nint hWnd, int msg, nint wParam, nint lParam)
    {
        _taskbarService?.HandleWindowMessage(msg, wParam, lParam);
        return NativeMethods.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }
}
