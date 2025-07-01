using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using Windows.UI;
using WinRT;
using WinRT.Interop;

namespace Nagi.Popups;

public sealed partial class TrayPopup : Window {
    public event EventHandler? Deactivated;

    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _configurationSource;
    private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

    public TrayPopup() {
        this.InitializeComponent();

        if (this.Content is Panel rootPanel) {
            // The root panel must be transparent for the system backdrop to be visible.
            rootPanel.Background = new SolidColorBrush(Colors.Transparent);
            rootPanel.ActualThemeChanged += OnThemeChanged;
        }

        ConfigureWindowAppearance();

        TrySetAcrylicBackdrop(DesktopAcrylicKind.Base);

        Activated += OnActivated;
    }

    private void ConfigureWindowAppearance() {
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);

        var windowHandle = WindowNative.GetWindowHandle(this);

        // Apply the tool window style to prevent the popup from appearing in the taskbar or Alt+Tab switcher.
        int exStyle = GetWindowLong(windowHandle, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(windowHandle, GWL_EXSTYLE, exStyle);

        // Request rounded corners for the window.
        var preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(windowHandle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(uint));
    }

    private bool TrySetAcrylicBackdrop(DesktopAcrylicKind kind) {
        if (!DesktopAcrylicController.IsSupported()) {
            return false;
        }

        _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
        _wsdqHelper.EnsureWindowsSystemDispatcherQueueController();

        _configurationSource = new SystemBackdropConfiguration();
        this.Closed += (sender, args) => {
            _acrylicController?.Dispose();
            _acrylicController = null;
            _configurationSource = null;
            _wsdqHelper = null;
            if (this.Content is Panel rootPanel) {
                rootPanel.ActualThemeChanged -= OnThemeChanged;
            }
        };

        _acrylicController = new DesktopAcrylicController();
        _acrylicController.Kind = kind;

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_configurationSource);

        SetConfigurationSourceTheme();
        return true;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) {
        SetConfigurationSourceTheme();
    }

    private void SetConfigurationSourceTheme() {
        if (_configurationSource == null || this.Content is not FrameworkElement root) return;

        // Update the backdrop theme to match the app's current theme.
        switch (root.ActualTheme) {
            case ElementTheme.Dark:
                _configurationSource.Theme = SystemBackdropTheme.Dark;
                break;
            case ElementTheme.Light:
                _configurationSource.Theme = SystemBackdropTheme.Light;
                break;
            case ElementTheme.Default:
                _configurationSource.Theme = SystemBackdropTheme.Default;
                break;
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args) {
        if (_configurationSource != null) {
            _configurationSource.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        }

        // When the window loses focus, raise the Deactivated event.
        if (args.WindowActivationState == WindowActivationState.Deactivated) {
            Deactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    #region P/Invoke for Window Styling

    // This helper class is needed for the SystemBackdropController to work correctly.
    private class WindowsSystemDispatcherQueueHelper {
        [StructLayout(LayoutKind.Sequential)]
        struct DispatcherQueueOptions {
            internal int dwSize;
            internal int threadType;
            internal int apartmentType;
        }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController([In] DispatcherQueueOptions options, [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);

        object? _dispatcherQueueController = null;
        public void EnsureWindowsSystemDispatcherQueueController() {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) {
                // one already exists, so we'll just use it.
                return;
            }

            if (_dispatcherQueueController == null) {
                DispatcherQueueOptions options;
                options.dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions));
                options.threadType = 2;    // DQTYPE_THREAD_CURRENT
                options.apartmentType = 2; // DQTAT_COM_STA

                CreateDispatcherQueueController(options, ref _dispatcherQueueController);
            }
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_ROUND = 2;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint pvAttribute, uint cbAttribute);

    #endregion
}