using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nagi.Services.Abstractions;
using Nagi.ViewModels;
using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Nagi.Popups;

/// <summary>
/// Represents the custom popup window that displays the player UI.
/// This window is configured to be borderless, non-resizable, and always on top,
/// with custom styling applied via Win32 interop.
/// </summary>
public sealed partial class TrayPopup : Window {
    public event EventHandler? Deactivated;
    public PlayerViewModel ViewModel { get; }

    public TrayPopup(ElementTheme initialTheme) {
        this.InitializeComponent();

        ViewModel = App.Services.GetRequiredService<PlayerViewModel>();

        if (this.Content is FrameworkElement rootElement) {
            rootElement.RequestedTheme = initialTheme;
            rootElement.DataContext = ViewModel;
            if (rootElement is Border rootBorder) {
                // Ensure the root border is transparent to allow the system backdrop to show through.
                rootBorder.Background = new SolidColorBrush(Colors.Transparent);
            }
            rootElement.UpdateLayout();
        }

        ConfigureWindowAppearance();

        Activated += OnActivated;
    }

    /// <summary>
    /// Sets the opacity of the window using layered window attributes.
    /// </summary>
    /// <param name="alpha">The opacity value, from 0 (transparent) to 255 (opaque).</param>
    public void SetWindowOpacity(byte alpha) {
        var hwnd = WindowNative.GetWindowHandle(this);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>
    /// Measures the desired height of the window's content for a given width.
    /// </summary>
    /// <param name="targetWidthDips">The width to constrain the content to.</param>
    /// <returns>The calculated desired height of the content.</returns>
    public double GetContentDesiredHeight(double targetWidthDips) {
        if (this.Content is FrameworkElement rootElement) {
            rootElement.Measure(new Size(targetWidthDips, double.PositiveInfinity));
            return rootElement.DesiredSize.Height;
        }
        return 0;
    }

    /// <summary>
    /// Configures the window's appearance using Win32 APIs to create a custom,
    /// borderless, and layered popup.
    /// </summary>
    private void ConfigureWindowAppearance() {
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);

        var windowHandle = WindowNative.GetWindowHandle(this);

        // Apply extended window styles for a custom appearance.
        int exStyle = GetWindowLong(windowHandle, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW; // Hides the window from the taskbar and Alt+Tab.
        exStyle |= WS_EX_LAYERED;    // Enables transparency and alpha blending.
        SetWindowLong(windowHandle, GWL_EXSTYLE, exStyle);

        // Request rounded corners from the Desktop Window Manager (DWM).
        var preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(windowHandle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(uint));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args) {
        if (args.WindowActivationState == WindowActivationState.Deactivated) {
            Deactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    #region Win32 Interop

    // Window Styles
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;

    // Layered Window Attributes
    private const uint LWA_ALPHA = 0x00000002;

    // DWM Window Attributes
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_ROUND = 2; // Prefer rounded corners.

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

    #endregion
}