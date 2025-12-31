using System;
using Windows.System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.ViewModels;
using WinRT.Interop;

namespace Nagi.WinUI.Popups;

/// <summary>
///     Represents the custom window used as a popup for the system tray icon.
/// </summary>
public sealed partial class TrayPopup : Window
{
    private readonly ILogger<TrayPopup> _logger;
    private readonly IUISettingsService _settingsService;
    private bool _isCoverArtInFlyoutEnabled;

    public TrayPopup(ElementTheme initialTheme)
    {
        InitializeComponent();

        ViewModel = App.Services!.GetRequiredService<PlayerViewModel>();
        _settingsService = App.Services!.GetRequiredService<IUISettingsService>();
        _logger = App.Services!.GetRequiredService<ILogger<TrayPopup>>();

        if (Content is Border rootBorder)
        {
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

        _logger.LogDebug("TrayPopup initialized.");
    }

    /// <summary>
    ///     Gets the PlayerViewModel to bind UI elements to playback state.
    /// </summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>
    ///     Occurs when the popup window loses focus and should be hidden.
    /// </summary>
    public event EventHandler? Deactivated;

    /// <summary>
    ///     Sets the opacity of the layered window.
    /// </summary>
    public void SetWindowOpacity(byte alpha)
    {
        _logger.LogTrace("Setting window opacity to {Alpha}", alpha);
        var hwnd = WindowNative.GetWindowHandle(this);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>
    ///     Calculates the desired height of the popup's content for a given width.
    /// </summary>
    public double GetContentDesiredHeight(double targetWidthDips)
    {
        if (Content is FrameworkElement rootElement)
        {
            rootElement.Measure(new Size(targetWidthDips, double.PositiveInfinity));
            return rootElement.DesiredSize.Height;
        }

        return 0;
    }

    private async Task InitializeSettingsAsync()
    {
        _logger.LogDebug("Initializing settings for tray popup...");
        try
        {
            _isCoverArtInFlyoutEnabled = await _settingsService.GetShowCoverArtInTrayFlyoutAsync();
            UpdateCoverArtVisibility();
            _logger.LogDebug("Successfully initialized settings. ShowCoverArtInTrayFlyout is {IsEnabled}.",
                _isCoverArtInFlyoutEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize tray popup settings.");
        }
    }

    private void UpdateCoverArtVisibility()
    {
        var shouldBeVisible = _isCoverArtInFlyoutEnabled && !string.IsNullOrEmpty(ViewModel.AlbumArtUri);
        CoverArtBackground.Visibility = shouldBeVisible ? Visibility.Visible : Visibility.Collapsed;
        
        // Update the brush source safely - SafeGetImageSource handles null/empty strings
        CoverArtBrush.Source = ImageUriHelper.SafeGetImageSource(ViewModel.AlbumArtUri)!;
        
        _logger.LogTrace("Cover art visibility updated to {Visibility}", CoverArtBackground.Visibility);
    }

    private void ConfigureWindowAppearance()
    {
        _logger.LogDebug("Configuring custom window appearance (tool window, layered, rounded corners).");
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);

        var windowHandle = WindowNative.GetWindowHandle(this);

        var exStyle = (int)GetWindowLongPtr(windowHandle, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle |= WS_EX_LAYERED;
        SetWindowLongPtr(windowHandle, GWL_EXSTYLE, (IntPtr)exStyle);

        if (Environment.OSVersion.Version.Build >= 22000)
        {
            var preference = DWMWCP_ROUND; // DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND
            DwmSetWindowAttribute(windowHandle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(uint));
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _logger.LogDebug("Tray popup deactivated. Firing Deactivated event.");
            Deactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnShowCoverArtSettingChanged(bool isEnabled)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _logger.LogDebug("'ShowCoverArtInTrayFlyout' setting changed to {IsEnabled}. Updating visibility.",
                isEnabled);
            _isCoverArtInFlyoutEnabled = isEnabled;
            UpdateCoverArtVisibility();
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.AlbumArtUri))
        {
            _logger.LogDebug("AlbumArtUri property changed. Updating cover art visibility.");
            UpdateCoverArtVisibility();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _logger.LogDebug("TrayPopup closed. Unsubscribing from events.");
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _settingsService.ShowCoverArtInTrayFlyoutSettingChanged -= OnShowCoverArtSettingChanged;
    }

    private void MediaSeekerSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.IsUserDraggingSlider = true;
    }

    private void MediaSeekerSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.IsUserDraggingSlider = false;
    }

    private void MediaSeekerSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.IsUserDraggingSlider = false;
    }

    private void MediaSeekerSlider_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
        {
            ViewModel.IsUserDraggingSlider = true;
        }
    }

    private void MediaSeekerSlider_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
        {
            ViewModel.IsUserDraggingSlider = false;
        }
    }

    #region Win32 Interop

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_ROUND = 2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint pvAttribute,
        uint cbAttribute);

    #endregion
}