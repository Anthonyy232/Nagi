using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Pages;
using WinRT.Interop;

namespace Nagi.WinUI;

/// <summary>
/// The main application window, responsible for hosting page content and managing the custom title bar.
/// </summary>
public sealed partial class MainWindow : Window {
    private AppWindow? _appWindow;

    // This flag ensures the title bar is initialized only once upon first activation,
    // preventing race conditions or redundant work during the application's startup sequence.
    private bool _isTitleBarInitialized = false;

    public MainWindow() {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Configures the window's title bar based on the current page's content.
    /// This should be called after navigation to ensure the title bar is appropriate for the visible page.
    /// </summary>
    public void InitializeCustomTitleBar() {
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null) {
            // A custom title bar cannot be configured without the AppWindow, which is a critical failure.
            Debug.WriteLine("[CRITICAL] AppWindow is not available. Cannot initialize custom title bar.");
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

            // Show or hide system caption buttons based on the page's preference.
            bool showSystemButtons = Content is not OnboardingPage;
            presenter.SetBorderAndTitleBar(true, showSystemButtons);

            // Safeguard to ensure the title bar's containing row has its height restored.
            if (provider.GetAppTitleBarRowElement() is { } titleBarRow) {
                titleBarRow.Height = new GridLength(48);
            }
        }
        else {
            // Revert to the default title bar if the current page does not provide a custom one.
            RevertToDefaultTitleBar(presenter);
        }
    }

    /// <summary>
    /// Reverts the window to use the standard system-drawn title bar.
    /// </summary>
    private void RevertToDefaultTitleBar(OverlappedPresenter? presenter = null) {
        ExtendsContentIntoTitleBar = false;
        SetTitleBar(null);
        // Restore the system-drawn border and caption buttons for a standard look.
        presenter?.SetBorderAndTitleBar(false, true);
    }

    // Handles the window's activation state changes.
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) {
        // The title bar must be initialized after the window is first activated,
        // as calling SetTitleBar before this point can fail silently.
        if (!_isTitleBarInitialized) {
            InitializeCustomTitleBar();
            _isTitleBarInitialized = true;
        }

        // Forward activation changes to the MainPage if it is the active content.
        // This allows the page to update its UI, such as dimming the title bar when focus is lost.
        if (Content is MainPage mainPage) {
            mainPage.UpdateActivationVisualState(args.WindowActivationState);
        }
    }

    // Unsubscribes from events when the window is closed to prevent memory leaks.
    private void MainWindow_Closed(object sender, WindowEventArgs args) {
        Activated -= MainWindow_Activated;
        Closed -= MainWindow_Closed;
    }

    // Retrieves the AppWindow instance for the current WinUI window.
    private AppWindow? GetAppWindowForCurrentWindow() {
        try {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            if (hWnd == IntPtr.Zero) return null;
            WindowId myWndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(myWndId);
        }
        catch (Exception ex) {
            // This can fail if the window is being closed or is otherwise unavailable.
            Debug.WriteLine($"[ERROR] Failed to retrieve AppWindow: {ex.Message}");
            return null;
        }
    }
}