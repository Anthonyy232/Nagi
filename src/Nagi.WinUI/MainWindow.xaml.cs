using System;
using System.Diagnostics;
using Windows.Graphics;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Models;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using WinRT;
using WinRT.Interop;

namespace Nagi.WinUI;

/// <summary>
///     The main application window. It manages the window frame, hosts application pages,
///     and dynamically configures a custom or default title bar based on the current content.
///     It also manages the window's backdrop material based on user settings and system theme.
/// </summary>
public sealed partial class MainWindow : Window
{
    private DesktopAcrylicController? _acrylicController;
    private AppWindow? _appWindow;

    private SystemBackdropConfiguration? _backdropConfiguration;
    private bool _isTitleBarInitialized;
    private MicaController? _micaController;
    private FrameworkElement? _rootElement;
    private IUISettingsService? _settingsService;
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
    }

    /// <summary>
    ///     Initializes the window with required services and subscribes to necessary events.
    /// </summary>
    /// <param name="settingsService">The service for UI-related settings.</param>
    public void InitializeDependencies(IUISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        TrySetBackdrop();

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;

        _settingsService.BackdropMaterialChanged += OnBackdropMaterialChanged;
        _settingsService.TransparencyEffectsSettingChanged += OnTransparencyEffectsChanged;
    }

    /// <summary>
    ///     Notifies the window that its content has been loaded. This allows the window to
    ///     hook into the content's theme change events to keep the title bar and backdrop synchronized.
    ///     This method should be called from App.xaml.cs after the initial page is set.
    /// </summary>
    public void NotifyContentLoaded()
    {
        // Unsubscribe from any previous content's event handler to prevent memory leaks.
        if (_rootElement != null) _rootElement.ActualThemeChanged -= OnActualThemeChanged;

        _rootElement = Content as FrameworkElement;
        if (_rootElement != null) _rootElement.ActualThemeChanged += OnActualThemeChanged;

        // Immediately synchronize the backdrop and title bar with the new content's current theme.
        SetBackdropTheme();
        UpdateTitleBarTheme();
    }

    /// <summary>
    ///     Configures the window's title bar. It uses a custom title bar if the current
    ///     page provides one; otherwise, it reverts to the default system title bar.
    /// </summary>
    public void InitializeCustomTitleBar()
    {
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null)
        {
            Debug.WriteLine("[CRITICAL] MainWindow: AppWindow is not available. Cannot initialize title bar.");
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

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        // Lazily initialize the title bar on first activation.
        if (!_isTitleBarInitialized)
        {
            InitializeCustomTitleBar();
            _isTitleBarInitialized = true;
        }

        if (_backdropConfiguration != null)
            _backdropConfiguration.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;

        if (Content is MainPage mainPage) mainPage.UpdateActivationVisualState(args.WindowActivationState);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Unsubscribe from all events and dispose of resources to prevent memory leaks.
        Activated -= OnWindowActivated;
        Closed -= OnWindowClosed;

        if (_rootElement != null) _rootElement.ActualThemeChanged -= OnActualThemeChanged;

        if (_settingsService != null)
        {
            _settingsService.BackdropMaterialChanged -= OnBackdropMaterialChanged;
            _settingsService.TransparencyEffectsSettingChanged -= OnTransparencyEffectsChanged;
        }

        _micaController?.Dispose();
        _micaController = null;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _wsdqHelper?.Dispose();
        _wsdqHelper = null;
        _backdropConfiguration = null;
    }

    private void OnBackdropMaterialChanged(BackdropMaterial material)
    {
        DispatcherQueue.TryEnqueue(() => TrySetBackdrop(material));
    }

    private void OnTransparencyEffectsChanged(bool isEnabled)
    {
        DispatcherQueue.TryEnqueue(() => TrySetBackdrop());
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        SetBackdropTheme();
        UpdateTitleBarTheme();
    }

    /// <summary>
    ///     Attempts to set the system backdrop (Mica or Acrylic) based on user settings.
    ///     Disposes of any existing backdrop controller before applying a new one.
    /// </summary>
    private async void TrySetBackdrop(BackdropMaterial? material = null)
    {
        if (_settingsService is null) return;

        material ??= await _settingsService.GetBackdropMaterialAsync();

        // Dispose of existing controllers before creating a new one.
        _micaController?.Dispose();
        _micaController = null;
        _acrylicController?.Dispose();
        _acrylicController = null;
        SystemBackdrop = null;

        if (_settingsService.IsTransparencyEffectsEnabled())
        {
            if (!EnsureWindowsSystemDispatcherQueue())
            {
                Debug.WriteLine(
                    "[WARN] MainWindow: Could not create a system dispatcher queue. Backdrop will not be applied.");
                return;
            }

            _backdropConfiguration = new SystemBackdropConfiguration { IsInputActive = true };
            SetBackdropTheme();

            switch (material)
            {
                case BackdropMaterial.Mica:
                    _micaController = new MicaController { Kind = MicaKind.Base };
                    _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
                    _micaController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
                    break;
                case BackdropMaterial.MicaAlt:
                    _micaController = new MicaController { Kind = MicaKind.BaseAlt };
                    _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
                    _micaController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
                    break;
                case BackdropMaterial.Acrylic:
                    _acrylicController = new DesktopAcrylicController();
                    _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
                    _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
                    break;
            }
        }
        else
        {
            _backdropConfiguration = null;
        }
    }

    private void SetBackdropTheme()
    {
        if (_backdropConfiguration != null && _rootElement != null)
            _backdropConfiguration.Theme = _rootElement.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default
            };
    }

    private void UpdateTitleBarTheme()
    {
        if (_appWindow?.TitleBar is not { } titleBar || _rootElement is null) return;

        // Set button colors to match the element theme.
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

    private bool EnsureWindowsSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return true;

        if (_wsdqHelper == null)
        {
            _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
            _wsdqHelper.EnsureDispatcherQueue();
        }

        return _wsdqHelper != null;
    }

    /// <summary>
    ///     Retrieves the AppWindow for the current WinUI Window instance.
    /// </summary>
    private AppWindow? GetAppWindowForCurrentWindow()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            if (hWnd == IntPtr.Zero)
            {
                Debug.WriteLine("[ERROR] MainWindow: Could not get window handle.");
                return null;
            }

            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(windowId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] MainWindow: Failed to retrieve AppWindow. Exception: {ex.Message}");
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
    private const int InitialWindowSize = 350;
    private const int MinWindowSize = 200;
    private const int MaxWindowSize = 640;
    private const string AppIconPath = "Assets/AppLogo.ico";
    private const int HorizontalScreenMargin = 10;
    private const int VerticalScreenMargin = 48;
    private readonly AppWindow _appWindow;

    private readonly MiniPlayerView _view;

    public MiniPlayerWindow()
    {
        _view = new MiniPlayerView(this);
        Content = _view;
        _appWindow = AppWindow;

        ConfigureAppWindow();
        SubscribeToEvents();
    }

    private void ConfigureAppWindow()
    {
        ExtendsContentIntoTitleBar = true;

        _appWindow.Title = "Nagi";
        _appWindow.SetIcon(AppIconPath);
        _appWindow.Resize(new SizeInt32(InitialWindowSize, InitialWindowSize));
        PositionWindowInBottomRight(_appWindow);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }
        else
        {
            Debug.WriteLine(
                "[WARN] MiniPlayerWindow: Could not configure presenter. It is not an OverlappedPresenter.");
        }
    }

    private void PositionWindowInBottomRight(AppWindow appWindow)
    {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea == null)
        {
            Debug.WriteLine("[WARN] MiniPlayerWindow: Could not retrieve display area to position the window.");
            return;
        }

        var workArea = displayArea.WorkArea;
        var positionX = workArea.X + workArea.Width - InitialWindowSize - HorizontalScreenMargin;
        var positionY = workArea.Y + workArea.Height - InitialWindowSize - VerticalScreenMargin;
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
    ///     clamping the size between a defined min and max.
    /// </summary>
    private void MaintainSquareAspectRatio(AppWindow window)
    {
        var currentPosition = window.Position;
        var currentSize = window.Size;

        var desiredSize = Math.Max(currentSize.Width, currentSize.Height);
        var newSize = Math.Clamp(desiredSize, MinWindowSize, MaxWindowSize);

        if (currentSize.Width == newSize && currentSize.Height == newSize) return;

        // To avoid the window "jumping" during resize, calculate the new top-left
        // position to keep the window centered on its previous location.
        var centerX = currentPosition.X + currentSize.Width / 2;
        var centerY = currentPosition.Y + currentSize.Height / 2;
        var newX = centerX - newSize / 2;
        var newY = centerY - newSize / 2;

        // Temporarily unsubscribe from the event to prevent recursion while we programmatically resize.
        window.Changed -= OnAppWindowChanged;
        window.MoveAndResize(new RectInt32(newX, newY, newSize, newSize));
        window.Changed += OnAppWindowChanged;
    }

    private void OnRestoreButtonClicked(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _view.RestoreButtonClicked -= OnRestoreButtonClicked;
        _appWindow.Changed -= OnAppWindowChanged;
        Closed -= OnWindowClosed;
    }
}