using System.ComponentModel;
using Windows.Foundation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;
using WinRT.Interop;
using Nagi.Core.Settings;
using Nagi.Presentation.Services;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Popups;

public sealed partial class TrayPopup : Window
{
    private readonly IThemeService _themeService;
    private readonly ISettingsService _settingsService;
    private bool _isClosed;

    public TrayPopup(PlayerViewModel viewModel, IThemeService themeService, ISettingsService settingsService)
    {
        InitializeComponent();

        ViewModel = viewModel;
        _themeService = themeService;
        _settingsService = settingsService;

        ApplyTheme();
        OuterBorder.DataContext = ViewModel;
        PlayerInputHelper.AttachSeekSlider(MediaSeekerSlider, ViewModel);

        ConfigureWindowAppearance();
        UpdateCoverArtVisibility();

        Activated += OnActivated;
        Closed += OnClosed;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settingsService.SettingChanged += OnSettingChanged;
        _themeService.ActualThemeChanged += ApplyTheme;
    }

    public PlayerViewModel ViewModel { get; }

    public event EventHandler? Deactivated;

    // The popup is kept and reused, so it follows theme changes made while it exists.
    private void ApplyTheme() =>
        OuterBorder.RequestedTheme = _themeService.IsLightTheme ? ElementTheme.Light : ElementTheme.Dark;

    public void SetWindowOpacity(byte alpha)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
    }

    public void ShowInForeground()
    {
        AppWindow.Show();
        NativeMethods.SetForegroundWindow(WindowNative.GetWindowHandle(this));
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

    private void UpdateCoverArtVisibility()
    {
        var shouldBeVisible = _settingsService.Get(AppSettings.ShowCoverArtInTrayFlyout) && !string.IsNullOrEmpty(ViewModel.AlbumArtUri);
        CoverArtBackground.Visibility = shouldBeVisible ? Visibility.Visible : Visibility.Collapsed;

        CoverArtBrush.Source = BitmapImageFactory.Create(ViewModel.AlbumArtUri)!;
    }

    private void ConfigureWindowAppearance()
    {
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);

        AppWindow.IsShownInSwitchers = false;

        var windowHandle = WindowNative.GetWindowHandle(this);

        var exStyle = NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(windowHandle, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_LAYERED);

        if (NativeMethods.IsWindows11OrNewer)
        {
            var preference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(windowHandle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference, sizeof(uint));
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Deactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting != AppSettings.ShowCoverArtInTrayFlyout) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed) UpdateCoverArtVisibility();
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.AlbumArtUri))
        {
            UpdateCoverArtVisibility();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _settingsService.SettingChanged -= OnSettingChanged;
        _themeService.ActualThemeChanged -= ApplyTheme;
    }

    private void VolumeButton_PointerWheelChanged(object sender, PointerRoutedEventArgs e) =>
        PlayerInputHelper.StepVolumeWithWheel(sender, e, ViewModel);
}
