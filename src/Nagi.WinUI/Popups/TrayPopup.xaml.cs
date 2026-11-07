using Windows.Foundation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;
using WinRT.Interop;

namespace Nagi.WinUI.Popups;

public sealed partial class TrayPopup : Window
{
    private readonly IThemeService _themeService;

    public TrayPopup(PlayerViewModel viewModel, IThemeService themeService)
    {
        ViewModel = viewModel;
        _themeService = themeService;
        InitializeComponent();

        ApplyTheme();
        OuterBorder.DataContext = ViewModel;
        PlayerInputHelper.AttachSeekSlider(MediaSeekerSlider, ViewModel);

        ConfigureWindowAppearance();

        Activated += OnActivated;
        Closed += OnClosed;
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

    private void OnClosed(object sender, WindowEventArgs args) => _themeService.ActualThemeChanged -= ApplyTheme;

    private void VolumeButton_PointerWheelChanged(object sender, PointerRoutedEventArgs e) =>
        PlayerInputHelper.StepVolumeWithWheel(sender, e, ViewModel);
}
