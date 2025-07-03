using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nagi.Services.Abstractions;
using Nagi.ViewModels;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI;
using WinRT.Interop;

namespace Nagi.Popups;

/// <summary>
/// Represents the popup window that appears when the tray icon is clicked.
/// This window is styled as a borderless tool window and supports dynamic content sizing.
/// </summary>
public sealed partial class TrayPopup : Window {
    public event EventHandler? Deactivated;

    public PlayerViewModel ViewModel { get; }

    private readonly ISettingsService _settingsService;
    private bool _isCoverArtInFlyoutEnabled;

    public TrayPopup(ElementTheme initialTheme) {
        this.InitializeComponent();

        ViewModel = App.Services.GetRequiredService<PlayerViewModel>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();

        if (this.Content is Border rootBorder) {
            //
            // Proactively set the theme on the root UI element. This prevents a visual flash
            // by ensuring the SystemBackdrop initializes with the correct theme from frame zero.
            //
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
    /// Loads the initial setting for cover art visibility and updates the UI.
    /// </summary>
    private async System.Threading.Tasks.Task InitializeSettingsAsync() {
        _isCoverArtInFlyoutEnabled = await _settingsService.GetShowCoverArtInTrayFlyoutAsync();
        UpdateCoverArtVisibility();
    }

    /// <summary>
    /// Updates the visibility of the cover art background based on the current setting and album art availability.
    /// </summary>
    private void UpdateCoverArtVisibility() {
        var shouldBeVisible = _isCoverArtInFlyoutEnabled && ViewModel.AlbumArtSource != null;
        CoverArtBackground.Visibility = shouldBeVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Calculates the required height of the window content for a given width.
    /// This is used to dynamically size the popup before it is shown.
    /// </summary>
    /// <param name="targetWidthDips">The target width in device-independent pixels.</param>
    /// <returns>The desired height in device-independent pixels.</returns>
    public double GetContentDesiredHeight(double targetWidthDips) {
        if (this.Content is FrameworkElement rootElement) {
            rootElement.Measure(new Size(targetWidthDips, double.PositiveInfinity));
            return rootElement.DesiredSize.Height;
        }
        return 0;
    }

    /// <summary>
    /// Configures the window to appear as a borderless, non-resizable, always-on-top tool window.
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

        //
        // Apply extended window styles to make it a tool window (no taskbar icon).
        //
        int exStyle = GetWindowLong(windowHandle, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(windowHandle, GWL_EXSTYLE, exStyle);

        //
        // Set the window corner preference to rounded.
        //
        var preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(windowHandle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(uint));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args) {
        if (args.WindowActivationState == WindowActivationState.Deactivated) {
            Deactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Handles changes to the cover art setting, updating the UI on the dispatcher thread.
    /// </summary>
    private void OnShowCoverArtSettingChanged(bool isEnabled) {
        DispatcherQueue.TryEnqueue(() => {
            _isCoverArtInFlyoutEnabled = isEnabled;
            UpdateCoverArtVisibility();
        });
    }

    /// <summary>
    /// Listens for changes to the ViewModel's AlbumArtSource to update visibility.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(PlayerViewModel.AlbumArtSource)) {
            UpdateCoverArtVisibility();
        }
    }

    /// <summary>
    /// Unsubscribes from events when the window is closed to prevent memory leaks.
    /// </summary>
    private void OnClosed(object sender, WindowEventArgs args) {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _settingsService.ShowCoverArtInTrayFlyoutSettingChanged -= OnShowCoverArtSettingChanged;
    }

    #region Win32 Interop

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