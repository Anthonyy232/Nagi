using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Pages;
using Windows.Graphics;
using WinRT.Interop;

namespace Nagi.WinUI;

/// <summary>
/// The main application window, responsible for hosting page content and managing the custom title bar.
/// </summary>
public sealed partial class MainWindow : Window {
    private AppWindow? _appWindow;
    private bool _isTitleBarInitialized = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow() {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Configures the window's title bar based on the current page's content.
    /// </summary>
    public void InitializeCustomTitleBar() {
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null) {
            Debug.WriteLine("[CRITICAL] MainWindow: AppWindow is not available. Cannot initialize custom title bar.");
            return;
        }

        _appWindow.SetIcon("Assets/AppLogo.ico");

        if (_appWindow.Presenter is not OverlappedPresenter presenter) {
            Debug.WriteLine("[WARN] MainWindow: OverlappedPresenter not available. Reverting to default title bar.");
            RevertToDefaultTitleBar();
            return;
        }

        if (Content is ICustomTitleBarProvider provider && provider.GetAppTitleBarElement() is { } titleBarElement) {
            // Configure the custom title bar provided by the current page.
            Debug.WriteLine($"[INFO] MainWindow: Initializing custom title bar from page: {Content.GetType().Name}.");
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(titleBarElement);

            bool showSystemButtons = Content is not OnboardingPage;
            presenter.SetBorderAndTitleBar(true, showSystemButtons);

            if (provider.GetAppTitleBarRowElement() is { } titleBarRow) {
                // Restore height in case it was collapsed.
                titleBarRow.Height = new GridLength(48);
            }
        }
        else {
            // Revert to the default title bar if the current page does not provide one.
            Debug.WriteLine("[INFO] MainWindow: Current page does not provide a custom title bar. Reverting to default.");
            RevertToDefaultTitleBar(presenter);
        }
    }

    /// <summary>
    /// Reverts the window to use the standard system-drawn title bar.
    /// </summary>
    private void RevertToDefaultTitleBar(OverlappedPresenter? presenter = null) {
        ExtendsContentIntoTitleBar = false;
        SetTitleBar(null);
        presenter?.SetBorderAndTitleBar(false, true);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) {
        // The title bar must be initialized after the window is first activated.
        if (!_isTitleBarInitialized) {
            Debug.WriteLine("[INFO] MainWindow: First activation. Initializing custom title bar.");
            InitializeCustomTitleBar();
            _isTitleBarInitialized = true;
        }

        // Forward activation changes to the MainPage to update its UI (e.g., title bar color).
        if (Content is MainPage mainPage) {
            mainPage.UpdateActivationVisualState(args.WindowActivationState);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args) {
        Activated -= MainWindow_Activated;
        Closed -= MainWindow_Closed;
    }

    private AppWindow? GetAppWindowForCurrentWindow() {
        try {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            if (hWnd == IntPtr.Zero) {
                Debug.WriteLine("[ERROR] MainWindow: Could not get window handle (hWnd is zero).");
                return null;
            }
            WindowId myWndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(myWndId);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] MainWindow: Failed to retrieve AppWindow. Exception: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// A secondary window that serves as an always-on-top mini-player.
/// Its lifecycle is managed by the <see cref="Services.Implementations.WindowService"/>.
/// </summary>
public sealed class MiniPlayerWindow : Window {
    private const int WINDOW_WIDTH = 300;
    private const int WINDOW_HEIGHT = 300;
    private const string APP_ICON_PATH = "Assets/AppLogo.ico";
    private const int HORIZONTAL_MARGIN = 10;
    private const int VERTICAL_MARGIN = 48;

    private readonly MiniPlayerView _view;

    public MiniPlayerWindow() {
        _view = new MiniPlayerView();
        this.Content = _view;

        InitializeWindowSettings();
        ConfigureAppWindow();
        SubscribeToViewEvents();
    }

    private void InitializeWindowSettings() {
        ExtendsContentIntoTitleBar = true;
        var draggableRegion = _view.GetDraggableRegion();
        if (draggableRegion != null) {
            SetTitleBar(draggableRegion);
        }
    }

    private void ConfigureAppWindow() {
        var appWindow = this.AppWindow;

        appWindow.Title = "Nagi";
        appWindow.SetIcon(APP_ICON_PATH);
        appWindow.Resize(new SizeInt32(WINDOW_WIDTH, WINDOW_HEIGHT));
        PositionWindowInBottomRight(appWindow);

        if (appWindow.Presenter is OverlappedPresenter presenter) {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }
        else {
            Debug.WriteLine("[WARN] MiniPlayerWindow: Could not configure the presenter as it is not an OverlappedPresenter.");
        }
    }

    /// <summary>
    /// Calculates and sets the window's initial position to the bottom-right of the primary display.
    /// </summary>
    /// <param name="appWindow">The AppWindow to position.</param>
    private void PositionWindowInBottomRight(AppWindow appWindow) {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(appWindow.Id, 0) ?? DisplayArea.Primary;

        if (displayArea != null) {
            RectInt32 workArea = displayArea.WorkArea;

            // Calculate the top-left position for the window.
            // The calculation accounts for the work area's offset on multi-monitor setups.
            int positionX = workArea.X + workArea.Width - WINDOW_WIDTH - HORIZONTAL_MARGIN;
            int positionY = workArea.Y + workArea.Height - WINDOW_HEIGHT - VERTICAL_MARGIN;

            appWindow.Move(new PointInt32(positionX, positionY));
        }
        else {
            Debug.WriteLine("[WARN] MiniPlayerWindow: Could not retrieve display area to position the window.");
        }
    }

    private void SubscribeToViewEvents() {
        _view.RestoreButtonClicked += OnRestoreButtonClicked;
        this.Closed += OnWindowClosed;
    }

    private void OnRestoreButtonClicked(object? sender, EventArgs e) {
        this.Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) {
        _view.RestoreButtonClicked -= OnRestoreButtonClicked;
        this.Closed -= OnWindowClosed;
    }
}