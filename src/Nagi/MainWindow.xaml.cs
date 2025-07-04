using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Interfaces;
using Nagi.Pages;
using System;
using System.Diagnostics;
using WinRT.Interop;

namespace Nagi;

/// <summary>
/// The main application window, responsible for hosting page content and managing the custom title bar.
/// </summary>
public sealed partial class MainWindow : Window {
    private AppWindow? _appWindow;

    // This flag ensures the title bar is initialized only once upon first activation,
    // preventing race conditions during the application's startup sequence.
    private bool _isTitleBarInitialized = false;

    public MainWindow() {
        InitializeComponent();

        // Signals the intent to use a custom title bar.
        // The actual title bar is configured later based on the page content.
        ExtendsContentIntoTitleBar = true;

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Configures the window's title bar based on the current page content.
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
            // Revert to the default title bar if the presenter does not support customization.
            ExtendsContentIntoTitleBar = false;
            SetTitleBar(null);
            return;
        }

        // Revert to the default title bar if the current page does not provide a custom one.
        if (Content is not ICustomTitleBarProvider provider || provider.GetAppTitleBarElement() is not { } titleBarElement) {
            ExtendsContentIntoTitleBar = false;
            SetTitleBar(null);
            // Restore the system-drawn border and caption buttons for a standard look.
            presenter.SetBorderAndTitleBar(false, true);
            return;
        }

        // Configure the custom title bar provided by the current page.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBarElement);

        // Show or hide system caption buttons (minimize, maximize, close) based on the page's preference.
        // For example, the OnboardingPage may prefer a cleaner look without them.
        bool showSystemButtons = Content is not OnboardingPage;
        presenter.SetBorderAndTitleBar(true, showSystemButtons);

        // As a safeguard, ensure the title bar's containing row has its height restored.
        // Using a fixed height is safer than 'Auto' to prevent layout issues.
        if (provider.GetAppTitleBarRowElement() is { } titleBarRow) {
            titleBarRow.Height = new GridLength(48);
        }
    }

    // Handles the window's activation state changes.
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) {
        // The title bar must be initialized after the window is first activated,
        // as calling SetTitleBar before this can fail silently.
        if (!_isTitleBarInitialized) {
            InitializeCustomTitleBar();
            _isTitleBarInitialized = true;
        }

        // Forward activation changes to the MainPage if it is active.
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