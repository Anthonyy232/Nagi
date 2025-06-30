using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nagi.Interfaces;
using Nagi.Pages;
using WinRT.Interop;

namespace Nagi;

/// <summary>
///     The main window of the application, responsible for hosting content and managing the custom title bar.
/// </summary>
public sealed partial class MainWindow : Window
{
    private AppWindow? _appWindow;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        Activated += MainWindow_Activated;
    }

    /// <summary>
    ///     Initializes the custom title bar based on the current page. It configures the title bar
    ///     differently for the OnboardingPage (no system controls) versus the MainPage (full custom title bar).
    /// </summary>
    public void InitializeCustomTitleBar()
    {
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null)
        {
            Debug.WriteLine("[MainWindow] AppWindow is not available. Cannot initialize custom title bar.");
            return;
        }

        _appWindow.SetIcon("Assets/AppLogo.ico");

        if (_appWindow.Presenter is not OverlappedPresenter presenter)
        {
            Debug.WriteLine(
                "[MainWindow] OverlappedPresenter is not available. Cannot customize title bar visibility.");
            return;
        }

        var provider = Content as ICustomTitleBarProvider;

        if (Content is OnboardingPage)
        {
            // On the Onboarding page, hide the system title bar buttons for a cleaner, immersive experience.
            presenter.SetBorderAndTitleBar(true, false);
            Debug.WriteLine("[MainWindow] OnboardingPage detected. Hiding system title bar.");

            // Collapse the title bar row to remove its space from the layout.
            if (provider?.GetAppTitleBarRowElement() is { } titleBarRow) titleBarRow.Height = new GridLength(0);
        }
        else
        {
            // On all other pages, show the system title bar buttons.
            presenter.SetBorderAndTitleBar(true, true);
            Debug.WriteLine("[MainWindow] Standard page detected. Configuring custom title bar.");

            if (provider?.GetAppTitleBarElement() is not { } appTitleBarElement)
            {
                // Revert to the default title bar if a custom one isn't provided by the current page.
                ExtendsContentIntoTitleBar = false;
                return;
            }

            // Ensure the title bar row has its space restored.
            if (provider.GetAppTitleBarRowElement() is { } titleBarRow) titleBarRow.Height = GridLength.Auto;

            // Set the XAML element to act as the title bar. This allows the system to handle
            // dragging, double-clicking, and inserts the caption buttons (min, max, close).
            SetTitleBar(appTitleBarElement);
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Forward window activation changes to the MainPage to update its visual state.
        if (Content is MainPage mainPage) mainPage.UpdateActivationVisualState(args.WindowActivationState);
    }

    private AppWindow? GetAppWindowForCurrentWindow()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            if (hWnd == IntPtr.Zero) return null;
            var myWndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(myWndId);
        }
        catch (Exception ex)
        {
            // This can happen if the window is closing.
            Debug.WriteLine($"[MainWindow] Failed to get AppWindow: {ex.Message}");
            return null;
        }
    }
}