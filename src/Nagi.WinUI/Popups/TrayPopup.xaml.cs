using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.ViewModels;
using WinRT.Interop;

namespace Nagi.WinUI.Popups;

/// <summary>
/// Represents the custom window used as a popup for the system tray icon.
/// </summary>
public sealed partial class TrayPopup : Window {
    /// <summary>
    /// Occurs when the popup window loses focus and should be hidden.
    /// </summary>
    public event EventHandler? Deactivated;

    /// <summary>
    /// Gets the PlayerViewModel to bind UI elements to playback state.
    /// </summary>
    public PlayerViewModel ViewModel { get; }

    private readonly IUISettingsService _settingsService;
    private bool _isCoverArtInFlyoutEnabled;

    public TrayPopup(ElementTheme initialTheme) {
        this.InitializeComponent();

        ViewModel = App.Services!.GetRequiredService<PlayerViewModel>();
        _settingsService = App.Services!.GetRequiredService<IUISettingsService>();

        if (this.Content is Border rootBorder) {
            rootBorder.RequestedTheme = initialTheme;
            rootBorder.DataContext = ViewModel;
            rootBorder.Background = new SolidColorBrush(Colors.Transparent);
            rootBorder.UpdateLayout();
        }

        ConfigureWindowAppearance();
        _ = InitializeSettingsAsync();

        Activated += OnActivated;
        Closed += OnClosed;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settingsService.ShowCoverArtInTrayFlyoutSettingChanged += OnShowCoverArtSettingChanged;
    }

    /// <summary>
    /// Sets the opacity of the layered window.
    /// </summary>
    /// <param name="alpha">The opacity value, from 0 (transparent) to 255 (opaque).</param>
    public void SetWindowOpacity(byte alpha) {
        var hwnd = WindowNative.GetWindowHandle(this);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>
    /// Calculates the desired height of the popup's content for a given width.
    /// </summary>
    /// <param name="targetWidthDips">The target width in device-independent pixels.</param>
    /// <returns>The desired height of the content.</returns>
    public double GetContentDesiredHeight(double targetWidthDips) {
        if (this.Content is FrameworkElement rootElement) {
            rootElement.Measure(new Size(targetWidthDips, double.PositiveInfinity));
            return rootElement.DesiredSize.Height;
        }
        return 0;
    }

    private async Task InitializeSettingsAsync() {
        _isCoverArtInFlyoutEnabled = await _settingsService.GetShowCoverArtInTrayFlyoutAsync();
        UpdateCoverArtVisibility();
    }

    private void UpdateCoverArtVisibility() {
        var shouldBeVisible = _isCoverArtInFlyoutEnabled && !string.IsNullOrEmpty(ViewModel.AlbumArtUri);
        CoverArtBackground.Visibility = shouldBeVisible ? Visibility.Visible : Visibility.Collapsed;
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

        int exStyle = GetWindowLong(windowHandle, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle |= WS_EX_LAYERED;
        SetWindowLong(windowHandle, GWL_EXSTYLE, exStyle);

        var preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(windowHandle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(uint));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args) {
        if (args.WindowActivationState == WindowActivationState.Deactivated) {
            Deactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnShowCoverArtSettingChanged(bool isEnabled) {
        DispatcherQueue.TryEnqueue(() => {
            _isCoverArtInFlyoutEnabled = isEnabled;
            UpdateCoverArtVisibility();
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(PlayerViewModel.AlbumArtUri)) {
            UpdateCoverArtVisibility();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args) {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _settingsService.ShowCoverArtInTrayFlyoutSettingChanged -= OnShowCoverArtSettingChanged;
    }

    #region Win32 Interop
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_ROUND = 2;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint pvAttribute, uint cbAttribute);
    #endregion
}