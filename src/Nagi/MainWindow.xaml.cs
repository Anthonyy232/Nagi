using System;
using System.Diagnostics;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nagi.Interfaces;
using WinRT.Interop;

namespace Nagi;

/// <summary>
///     The main window of the application, responsible for hosting content and managing the custom title bar.
/// </summary>
public sealed partial class MainWindow : Window
{
    private AppWindow? _appWindow;
    private bool _isAppWindowListenerAttached;

    public MainWindow()
    {
        InitializeComponent();
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    /// <summary>
    ///     Initializes the custom title bar, extending the app's content into the title bar area.
    /// </summary>
    public void InitializeCustomTitleBar()
    {
        _appWindow ??= GetAppWindowForCurrentWindow();
        if (_appWindow == null)
        {
            // This is a critical failure for custom title bar initialization.
            Debug.WriteLine("[MainWindow] AppWindow is not available. Cannot initialize custom title bar.");
            return;
        }

        // Set the icon for the preview thumbnail
        _appWindow.SetIcon("Assets/AppLogo.ico");

        var titleBar = _appWindow.TitleBar;
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            // Fallback to the default title bar if customization is not supported by the OS.
            titleBar.ExtendsContentIntoTitleBar = false;
            return;
        }

        // Attempt to get the title bar element from the current page content.
        if (Content is not ICustomTitleBarProvider titleBarProvider || titleBarProvider.GetAppTitleBarElement() is not
                { } appTitleBarElement)
        {
            // If the current page doesn't provide a title bar, revert to the default.
            titleBar.ExtendsContentIntoTitleBar = false;
            return;
        }

        // Configure the custom title bar.
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        ApplyThemeToCaptionButtons(titleBar);
        SetTitleBar(appTitleBarElement);
        UpdateTitleBarLayout(titleBar);

        if (!_isAppWindowListenerAttached)
        {
            _appWindow.Changed += AppWindow_Changed;
            _isAppWindowListenerAttached = true;
        }
    }

    /// <summary>
    ///     Re-applies the theme colors to the window's caption buttons.
    ///     This should be called when the application theme changes.
    /// </summary>
    public void UpdateCaptionButtonColors()
    {
        if (_appWindow?.TitleBar != null && AppWindowTitleBar.IsCustomizationSupported())
            DispatcherQueue.TryEnqueue(() => { ApplyThemeToCaptionButtons(_appWindow.TitleBar); });
    }

    /// <summary>
    ///     Handles the window's Activated event to update visual states of the content.
    /// </summary>
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Forward the activation state to the MainPage to update its visuals (e.g., title bar text color).
        if (Content is MainPage mainPage) mainPage.UpdateActivationVisualState(args.WindowActivationState);
    }

    /// <summary>
    ///     Handles the window's Closed event to clean up resources and event handlers.
    /// </summary>
    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_appWindow != null && _isAppWindowListenerAttached)
        {
            _appWindow.Changed -= AppWindow_Changed;
            _isAppWindowListenerAttached = false;
        }

        _appWindow = null;
    }

    /// <summary>
    ///     Handles the AppWindow's Changed event to respond to size or visibility changes.
    /// </summary>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidVisibilityChange)
            // Ensure UI updates for layout are performed on the dispatcher queue.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (sender.TitleBar != null && AppWindowTitleBar.IsCustomizationSupported())
                    UpdateTitleBarLayout(sender.TitleBar);
            });
    }

    /// <summary>
    ///     Updates the height of the custom title bar element to match the system's title bar height.
    /// </summary>
    private void UpdateTitleBarLayout(AppWindowTitleBar titleBar)
    {
        if (Content is ICustomTitleBarProvider titleBarProvider)
        {
            var appTitleBarElement = titleBarProvider.GetAppTitleBarElement();
            if (appTitleBarElement != null)
                // Set the height of our custom title bar, with a fallback for safety.
                appTitleBarElement.Height = titleBar.Height > 0 ? titleBar.Height : 32;
        }
    }

    /// <summary>
    ///     Applies the correct colors to the window caption buttons (minimize, maximize, close) based on the current theme.
    /// </summary>
    private void ApplyThemeToCaptionButtons(AppWindowTitleBar titleBar)
    {
        if (Content is not FrameworkElement contentElement) return;

        var currentTheme = contentElement.ActualTheme;

        var fgColor = currentTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        var inactiveFgColor = currentTheme == ElementTheme.Dark
            ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x99, 0x00, 0x00, 0x00);
        var hoverBgColor = currentTheme == ElementTheme.Dark
            ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x15, 0x00, 0x00, 0x00);
        var pressedBgColor = currentTheme == ElementTheme.Dark
            ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x25, 0x00, 0x00, 0x00);

        titleBar.ButtonForegroundColor = fgColor;
        titleBar.ButtonHoverForegroundColor = fgColor;
        titleBar.ButtonPressedForegroundColor = fgColor;
        titleBar.ButtonInactiveForegroundColor = inactiveFgColor;
        titleBar.ButtonHoverBackgroundColor = hoverBgColor;
        titleBar.ButtonPressedBackgroundColor = pressedBgColor;
    }

    /// <summary>
    ///     Retrieves the AppWindow associated with this XAML Window.
    /// </summary>
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
            // This can happen if the window is in the process of being closed.
            Debug.WriteLine($"[MainWindow] Failed to get AppWindow. Window may be closed. {ex.Message}");
            return null;
        }
    }
}