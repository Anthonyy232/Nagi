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
    private const int WINDOW_SIZE = 200;

    public MiniPlayerWindow() {
        var view = new MiniPlayerView();
        this.Content = view;

        // Subscribe to the view's event to close this window.
        view.RestoreButtonClicked += (sender, args) => this.Close();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(view.GetDraggableRegion());

        var appWindow = this.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter presenter) {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
            Debug.WriteLine("[INFO] MiniPlayerWindow: Presenter configured for always-on-top, non-resizable state with custom title bar.");
        }
        appWindow.Resize(new SizeInt32(WINDOW_SIZE, WINDOW_SIZE));
        appWindow.Title = "Nagi Mini Player";
        appWindow.SetIcon("Assets/AppLogo.ico");
    }
}