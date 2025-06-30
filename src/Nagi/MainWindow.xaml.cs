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
///     The main window of the application, responsible for hosting content and managing the custom title bar.
/// </summary>
public sealed partial class MainWindow : Window {
    private AppWindow? _appWindow;

    public MainWindow() {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        Activated += MainWindow_Activated;
    }

    /// <summary>
    ///     Initializes the custom title bar based on the current page. It configures the title bar
    ///     differently for the OnboardingPage (no system controls) versus the MainPage (full custom title bar).
    /// </summary>
    public void InitializeCustomTitleBar() {
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null) {
            Debug.WriteLine("[MainWindow] AppWindow is not available. Cannot initialize custom title bar.");
            return;
        }

        _appWindow.SetIcon("Assets/AppLogo.ico");

        if (_appWindow.Presenter is not OverlappedPresenter presenter) {
            Debug.WriteLine(
                "[MainWindow] OverlappedPresenter is not available. Cannot customize title bar visibility.");
            return;
        }

        // Try to get the custom title bar provider from the current content.
        if (Content is not ICustomTitleBarProvider provider) {
            // If the current page doesn't provide a custom title bar, revert to the default.
            ExtendsContentIntoTitleBar = false;
            Debug.WriteLine("[MainWindow] No ICustomTitleBarProvider found. Reverting to default title bar.");
            return;
        }

        // Get the custom title bar element from the provider.
        if (provider.GetAppTitleBarElement() is not { } appTitleBarElement) {
            // If the provider exists but doesn't return an element, this is an issue. Revert.
            ExtendsContentIntoTitleBar = false;
            Debug.WriteLine("[MainWindow] ICustomTitleBarProvider did not provide a TitleBar element. Reverting.");
            return;
        }

        // Ensure we are extending content into the title bar area.
        ExtendsContentIntoTitleBar = true;

        // This is the crucial step: designate the XAML element as the title bar.
        // This makes it draggable and clickable. This must be done for ALL custom title bars.
        SetTitleBar(appTitleBarElement);
        Debug.WriteLine("[MainWindow] Custom title bar element set.");

        // Get the row definition to manage its visibility.
        var titleBarRow = provider.GetAppTitleBarRowElement();

        // Now, customize based on the specific page type.
        if (Content is OnboardingPage) {
            // On the Onboarding page, hide the system title bar buttons for a cleaner look.
            presenter.SetBorderAndTitleBar(true, false);
            Debug.WriteLine("[MainWindow] OnboardingPage detected. Hiding system caption buttons.");

            // Ensure the title bar row is visible so it can be dragged.
            // The row height is set to Auto in XAML, which is what we want.
            // We no longer set it to 0.
            if (titleBarRow is not null) titleBarRow.Height = GridLength.Auto;
        }
        else // For MainPage and any other pages
        {
            // On all other pages, show the system title bar buttons, which will be inserted into our custom title bar.
            presenter.SetBorderAndTitleBar(true, true);
            Debug.WriteLine("[MainWindow] Standard page detected. Showing system caption buttons.");

            // Ensure the title bar row has its space restored if it was ever changed.
            if (titleBarRow is not null) titleBarRow.Height = GridLength.Auto;
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) {
        // Forward window activation changes to the MainPage to update its visual state.
        if (Content is MainPage mainPage) mainPage.UpdateActivationVisualState(args.WindowActivationState);
    }

    private AppWindow? GetAppWindowForCurrentWindow() {
        try {
            var hWnd = WindowNative.GetWindowHandle(this);
            if (hWnd == IntPtr.Zero) return null;
            var myWndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(myWndId);
        }
        catch (Exception ex) {
            // This can happen if the window is closing.
            Debug.WriteLine($"[MainWindow] Failed to get AppWindow: {ex.Message}");
            return null;
        }
    }
}