using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Pages;
using System;
using System.Diagnostics;
using Windows.Graphics;
using WinRT.Interop;

namespace Nagi.WinUI;

/// <summary>
/// The main application window, responsible for hosting page content and managing a custom title bar.
/// </summary>
public sealed partial class MainWindow : Window {
    private AppWindow? _appWindow;
    private bool _isTitleBarInitialized = false;

    public MainWindow() {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
    }

    /// <summary>
    /// Configures the window's title bar based on the current page's content.
    /// This method should be called after the window has been activated.
    /// </summary>
    public void InitializeCustomTitleBar() {
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null) {
            // This is a critical failure, as the application cannot be controlled without an AppWindow.
            Debug.WriteLine("[CRITICAL] MainWindow: AppWindow is not available. Cannot initialize title bar.");
            return;
        }

        _appWindow.SetIcon("Assets/AppLogo.ico");

        if (_appWindow.Presenter is not OverlappedPresenter presenter) {
            RevertToDefaultTitleBar();
            return;
        }

        if (Content is ICustomTitleBarProvider provider && provider.GetAppTitleBarElement() is { } titleBarElement) {
            // Configure the custom title bar provided by the current page.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(titleBarElement);
            bool showSystemButtons = Content is not OnboardingPage;
            presenter.SetBorderAndTitleBar(true, showSystemButtons);
        }
        else {
            // Revert to the default title bar if the current page does not provide one.
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

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args) {
        // The title bar must be initialized after the window is first activated.
        if (!_isTitleBarInitialized) {
            InitializeCustomTitleBar();
            _isTitleBarInitialized = true;
        }

        // Forward activation changes to the MainPage to update its UI (e.g., title bar color).
        if (Content is MainPage mainPage) {
            mainPage.UpdateActivationVisualState(args.WindowActivationState);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) {
        Activated -= OnWindowActivated;
        Closed -= OnWindowClosed;
    }

    private AppWindow? GetAppWindowForCurrentWindow() {
        try {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            if (hWnd == IntPtr.Zero) {
                Debug.WriteLine("[ERROR] MainWindow: Could not get window handle.");
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
/// </summary>
public sealed class MiniPlayerWindow : Window {
    private const int WindowWidth = 350;
    private const int WindowHeight = 350;
    private const string AppIconPath = "Assets/AppLogo.ico";
    private const int HorizontalScreenMargin = 10;
    private const int VerticalScreenMargin = 48;

    private readonly MiniPlayerView _view;

    public MiniPlayerWindow() {
        _view = new MiniPlayerView(this);
        Content = _view;

        ConfigureAppWindow();
        SubscribeToEvents();
    }

    private void ConfigureAppWindow() {
        ExtendsContentIntoTitleBar = false;

        var appWindow = AppWindow;
        appWindow.Title = "Nagi";
        appWindow.SetIcon(AppIconPath);
        appWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));
        PositionWindowInBottomRight(appWindow);

        if (appWindow.Presenter is OverlappedPresenter presenter) {
            // Configure the window to be a tool window (always on top, not resizable, no system controls).
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

    private void PositionWindowInBottomRight(AppWindow appWindow) {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);

        if (displayArea != null) {
            var workArea = displayArea.WorkArea;
            int positionX = workArea.X + workArea.Width - WindowWidth - HorizontalScreenMargin;
            int positionY = workArea.Y + workArea.Height - WindowHeight - VerticalScreenMargin;
            appWindow.Move(new PointInt32(positionX, positionY));
        }
        else {
            Debug.WriteLine("[WARN] MiniPlayerWindow: Could not retrieve display area to position the window.");
        }
    }

    private void SubscribeToEvents() {
        _view.RestoreButtonClicked += OnRestoreButtonClicked;
        Closed += OnWindowClosed;
    }

    private void OnRestoreButtonClicked(object? sender, EventArgs e) {
        Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) {
        _view.RestoreButtonClicked -= OnRestoreButtonClicked;
        Closed -= OnWindowClosed;
    }
}