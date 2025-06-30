using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Interfaces;
using Nagi.Pages;
using WinRT.Interop;

namespace Nagi;

/// <summary>
/// The main application window, responsible for hosting page content and managing the custom title bar.
/// </summary>
public sealed partial class MainWindow : Window {
    private AppWindow? _appWindow;

    /// <summary>
    /// A flag to ensure the title bar is initialized only once upon first activation.
    /// This prevents race conditions during the application's startup sequence.
    /// </summary>
    private bool _isTitleBarInitialized = false;

    public MainWindow() {
        InitializeComponent();

        // Signal the intent to use a custom title bar. The actual title bar is
        // configured in InitializeCustomTitleBar based on the page content.
        ExtendsContentIntoTitleBar = true;

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Configures the window's title bar. This method adapts the title bar based on the
    /// current page, using a fully custom title bar for most pages but a modified
    /// version for special cases like the OnboardingPage.
    /// This should be called after every navigation to ensure the title bar reflects the current page.
    /// </summary>
    public void InitializeCustomTitleBar() {
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null) {
            // This is a critical failure; a custom title bar cannot be configured without the AppWindow.
            Debug.WriteLine("[CRITICAL] AppWindow is not available. Cannot initialize custom title bar.");
            return;
        }

        _appWindow.SetIcon("Assets/AppLogo.ico");

        if (_appWindow.Presenter is not OverlappedPresenter presenter) {
            Debug.WriteLine("[WARNING] OverlappedPresenter is not available. Cannot customize title bar.");
            // Revert to default title bar if we can't customize it.
            ExtendsContentIntoTitleBar = false;
            SetTitleBar(null);
            return;
        }

        // Revert to the default title bar if the page does not provide a custom one.
        if (Content is not ICustomTitleBarProvider provider || provider.GetAppTitleBarElement() is not { } titleBarElement) {
            ExtendsContentIntoTitleBar = false;
            SetTitleBar(null);
            // Restore system-drawn border and caption buttons.
            presenter.SetBorderAndTitleBar(false, true);
            return;
        }

        // At this point, all prerequisites are met. Configure the custom title bar.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBarElement);

        // Show or hide the system caption buttons (minimize, maximize, close) based on the page.
        // The OnboardingPage uses a cleaner look without these buttons.
        bool showSystemButtons = Content is not OnboardingPage;
        presenter.SetBorderAndTitleBar(true, showSystemButtons);

        // As a safeguard, ensure the title bar's containing row has its height restored.
        // Using a fixed height matching the XAML definition is safer than 'Auto'.
        if (provider.GetAppTitleBarRowElement() is { } titleBarRow) {
            titleBarRow.Height = new GridLength(48);
        }
    }

    /// <summary>
    /// Handles the window's activation state changes.
    /// </summary>
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) {
        // On the very first launch, the window is activated after the initial page has loaded.
        // Calling SetTitleBar before the window is active can fail silently.
        // This block ensures the title bar is correctly initialized upon first activation.
        if (!_isTitleBarInitialized) {
            InitializeCustomTitleBar();
            _isTitleBarInitialized = true;
        }

        // Forward window activation changes to the MainPage if it is active.
        // This allows the page to update its UI, for example, by dimming
        // the title bar when the window loses focus.
        if (Content is MainPage mainPage) {
            mainPage.UpdateActivationVisualState(args.WindowActivationState);
        }
    }

    /// <summary>
    /// Unsubscribes from events when the window is closed to prevent memory leaks.
    /// </summary>
    private void MainWindow_Closed(object sender, WindowEventArgs args) {
        Activated -= MainWindow_Activated;
        Closed -= MainWindow_Closed;
    }

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