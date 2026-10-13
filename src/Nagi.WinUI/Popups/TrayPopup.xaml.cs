using System;
using Windows.System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;
using WinRT.Interop;

namespace Nagi.WinUI.Popups;

public sealed partial class TrayPopup : Window
{
    private readonly ILogger<TrayPopup> _logger;
    private readonly IUISettingsService _settingsService;
    private readonly IWin32InteropService _win32InteropService;
    private bool _isClosed;
    private bool _isCoverArtInFlyoutEnabled;

    public TrayPopup(ElementTheme initialTheme, PlayerViewModel viewModel, IUISettingsService settingsService,
        IWin32InteropService win32InteropService, ILogger<TrayPopup> logger)
    {
        InitializeComponent();

        ViewModel = viewModel;
        _settingsService = settingsService;
        _win32InteropService = win32InteropService;
        _logger = logger;

        OuterBorder.RequestedTheme = initialTheme;
        OuterBorder.DataContext = ViewModel;

        ConfigureWindowAppearance();
        _ = InitializeSettingsAsync();

        Activated += OnActivated;
        Closed += OnClosed;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settingsService.ShowCoverArtInTrayFlyoutSettingChanged += OnShowCoverArtSettingChanged;

        _logger.LogDebug("TrayPopup initialized.");
    }

    public PlayerViewModel ViewModel { get; }

    public event EventHandler? Deactivated;

    public void SetWindowOpacity(byte alpha)
    {
        _logger.LogTrace("Setting window opacity to {Alpha}", alpha);
        var hwnd = WindowNative.GetWindowHandle(this);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    public async Task<double> ShowAndMeasureAsync(double targetWidthDips)
    {
        SetWindowOpacity(0);
        if (!OuterBorder.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(object sender, RoutedEventArgs args) => loaded.SetResult();

            OuterBorder.Loaded += OnLoaded;
            AppWindow.Show();
            await loaded.Task;
            OuterBorder.Loaded -= OnLoaded;
        }

        OuterBorder.Measure(new Size(targetWidthDips, double.PositiveInfinity));
        return OuterBorder.DesiredSize.Height;
    }

    private async Task InitializeSettingsAsync()
    {
        _logger.LogDebug("Initializing settings for tray popup...");
        try
        {
            _isCoverArtInFlyoutEnabled = await _settingsService.GetShowCoverArtInTrayFlyoutAsync();
            if (_isClosed) return;
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

        CoverArtBrush.Source = BitmapImageFactory.Create(ViewModel.AlbumArtUri)!;

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

        AppWindow.IsShownInSwitchers = false;

        var windowHandle = WindowNative.GetWindowHandle(this);

        var exStyle = (int)GetWindowLongPtr(windowHandle, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED;
        SetWindowLongPtr(windowHandle, GWL_EXSTYLE, (IntPtr)exStyle);

        if (_win32InteropService.IsWindows11OrNewer)
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
            if (_isClosed) return;

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
        _isClosed = true;
        _logger.LogDebug("TrayPopup closed. Unsubscribing from events.");
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _settingsService.ShowCoverArtInTrayFlyoutSettingChanged -= OnShowCoverArtSettingChanged;
    }

    private void VolumeButton_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(sender as UIElement).Properties;
        var delta = properties.MouseWheelDelta;

        double newVolume = ViewModel.CurrentVolume + (delta > 0 ? 5 : -5);
        ViewModel.CurrentVolume = Math.Clamp(newVolume, 0, 100);
        e.Handled = true;
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
