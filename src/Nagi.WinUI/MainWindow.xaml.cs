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
/// The main application window. It manages the window frame, hosts application pages,
/// and dynamically configures a custom or default title bar based on the current content.
/// </summary>
public sealed partial class MainWindow : Window {
    private AppWindow? _appWindow;
    private bool _isTitleBarInitialized = false;

    public MainWindow() {
        InitializeComponent();

        // Allow content to be drawn into the title bar area for a custom look.
        ExtendsContentIntoTitleBar = true;

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
    }

    /// <summary>
    /// Configures the window's title bar based on the current page's content.
    /// This method inspects the current page for an ICustomTitleBarProvider
    /// to determine how the title bar should be rendered.
    /// </summary>
    public void InitializeCustomTitleBar() {
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null) {
            // This is a critical failure, as the app cannot be controlled without an AppWindow.
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
        presenter ??= _appWindow?.Presenter as OverlappedPresenter;
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

            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(windowId);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] MainWindow: Failed to retrieve AppWindow. Exception: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// A secondary window that serves as an always-on-top, resizable mini-player.
/// It maintains a square aspect ratio and positions itself in the corner of the screen.
/// </summary>
public sealed class MiniPlayerWindow : Window {
    private const int InitialWindowSize = 350;
    private const int MinWindowSize = 200;
    private const int MaxWindowSize = 640;
    private const string AppIconPath = "Assets/AppLogo.ico";
    private const int HorizontalScreenMargin = 10;
    private const int VerticalScreenMargin = 48;

    private readonly MiniPlayerView _view;
    private readonly AppWindow _appWindow;

    public MiniPlayerWindow() {
        _view = new MiniPlayerView(this);
        Content = _view;
        _appWindow = this.AppWindow;

        ConfigureAppWindow();
        SubscribeToEvents();
    }

    private void ConfigureAppWindow() {
        ExtendsContentIntoTitleBar = true;

        _appWindow.Title = "Nagi";
        _appWindow.SetIcon(AppIconPath);
        _appWindow.Resize(new SizeInt32(InitialWindowSize, InitialWindowSize));
        PositionWindowInBottomRight(_appWindow);

        if (_appWindow.Presenter is OverlappedPresenter presenter) {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }
        else {
            Debug.WriteLine("[WARN] MiniPlayerWindow: Could not configure presenter. It is not an OverlappedPresenter.");
        }
    }

    private void PositionWindowInBottomRight(AppWindow appWindow) {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea == null) {
            Debug.WriteLine("[WARN] MiniPlayerWindow: Could not retrieve display area to position the window.");
            return;
        }

        var workArea = displayArea.WorkArea;
        int positionX = workArea.X + workArea.Width - InitialWindowSize - HorizontalScreenMargin;
        int positionY = workArea.Y + workArea.Height - InitialWindowSize - VerticalScreenMargin;
        appWindow.Move(new PointInt32(positionX, positionY));
    }

    private void SubscribeToEvents() {
        _view.RestoreButtonClicked += OnRestoreButtonClicked;
        _appWindow.Changed += OnAppWindowChanged;
        Closed += OnWindowClosed;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) {
        if (args.DidSizeChange) {
            MaintainSquareAspectRatio(sender);
        }
    }

    /// <summary>
    /// Enforces a 1:1 aspect ratio for the window, resizing it from the center
    /// to prevent a "dragging" effect during user resizing.
    /// </summary>
    private void MaintainSquareAspectRatio(AppWindow window) {
        PointInt32 currentPosition = window.Position;
        SizeInt32 currentSize = window.Size;

        int desiredSize = Math.Max(currentSize.Width, currentSize.Height);
        int newSize = Math.Clamp(desiredSize, MinWindowSize, MaxWindowSize);

        // If the window is already the correct square size, no change is needed.
        if (currentSize.Width == newSize && currentSize.Height == newSize) {
            return;
        }

        // Calculate the center point before the resize.
        int centerX = currentPosition.X + (currentSize.Width / 2);
        int centerY = currentPosition.Y + (currentSize.Height / 2);

        // Calculate the new top-left position to keep the window centered.
        int newX = centerX - (newSize / 2);
        int newY = centerY - (newSize / 2);

        // Temporarily unsubscribe from the event to prevent re-entrancy,
        // as we are about to change the size again.
        window.Changed -= OnAppWindowChanged;
        window.MoveAndResize(new RectInt32(newX, newY, newSize, newSize));
        window.Changed += OnAppWindowChanged;
    }

    private void OnRestoreButtonClicked(object? sender, EventArgs e) {
        Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) {
        // Clean up event subscriptions to prevent memory leaks.
        _view.RestoreButtonClicked -= OnRestoreButtonClicked;
        _appWindow.Changed -= OnAppWindowChanged;
        Closed -= OnWindowClosed;
    }
}