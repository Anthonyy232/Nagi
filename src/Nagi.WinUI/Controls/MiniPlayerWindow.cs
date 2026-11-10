using System.Threading.Tasks;
using Windows.Graphics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Presentation.Player;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Theme;
using Nagi.WinUI.Helpers;
using WinRT.Interop;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A secondary window that serves as an always-on-top, resizable mini-player.
///     It maintains a square aspect ratio and positions itself in the corner of the screen.
/// </summary>
public sealed class MiniPlayerWindow : Window
{
    private const int InitialWindowSizeDips = 350;
    private const int MinWindowSizeDips = 200;
    private const int MaxWindowSizeDips = 640;

    private const int HorizontalScreenMargin = 10;
    private const int VerticalScreenMargin = 48;

    private readonly AppWindow _appWindow;
    private readonly IThemeService _themeService;
    private readonly ILogger<MiniPlayerWindow> _logger;
    private readonly MiniPlayerView _view;

    public MiniPlayerWindow(PlayerViewModel playerViewModel, IThemeService themeService,
        ILogger<MiniPlayerWindow> logger)
    {
        _themeService = themeService;
        _logger = logger;
        _view = new MiniPlayerView(this, playerViewModel);
        Content = _view;
        _appWindow = AppWindow;

        SystemBackdrop = new DesktopAcrylicBackdrop();
        ApplyTheme();

        ConfigureAppWindow();
        SubscribeToEvents();
    }

    /// <summary>
    ///     Raised when the restore button asks for the main window back. The mini player is hidden but stays open
    ///     until it is closed.
    /// </summary>
    public event Action? RestoreRequested;

    private void ApplyTheme() =>
        _view.RequestedTheme = _themeService.IsLightTheme ? ElementTheme.Light : ElementTheme.Dark;

    private void ConfigureAppWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_view.TitleBarDragRegion);

        _appWindow.Title = Strings.MiniPlayer_Title;
        _appWindow.SetIcon(MainWindow.AppIconPath);

        // Convert logical pixels to physical pixels for AppWindow.Resize()
        var initialSize = (int)(InitialWindowSizeDips * GetDpiScale());
        _appWindow.Resize(new SizeInt32(initialSize, initialSize));
        PositionWindowInBottomRight(_appWindow, initialSize);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false); // Keep a border but hide the system title text.
        }
        else
        {
            _logger.LogWarning("Could not configure presenter. It is not an OverlappedPresenter");
        }
    }

    private void PositionWindowInBottomRight(AppWindow appWindow, int windowSize)
    {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea == null)
        {
            _logger.LogWarning("Could not retrieve display area to position the window");
            return;
        }

        // Use WorkArea to respect the user's taskbar position and avoid overlapping it.
        var workArea = displayArea.WorkArea;
        var positionX = workArea.X + workArea.Width - windowSize - HorizontalScreenMargin;
        var positionY = workArea.Y + workArea.Height - windowSize - VerticalScreenMargin;
        appWindow.Move(new PointInt32(positionX, positionY));
    }

    private void SubscribeToEvents()
    {
        _view.RestoreButtonClicked += OnRestoreButtonClicked;
        _appWindow.Changed += OnAppWindowChanged;
        _themeService.ActualThemeChanged += ApplyTheme;
        Closed += OnWindowClosed;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange) MaintainSquareAspectRatio(sender);
    }

    /// <summary>
    ///     Enforces a square aspect ratio for the window during resizing,
    ///     clamping the size between defined minimum and maximum values (in physical pixels).
    /// </summary>
    private void MaintainSquareAspectRatio(AppWindow window)
    {
        var currentPosition = window.Position;
        var currentSize = window.Size;

        var dpiScale = GetDpiScale();
        var minSize = (int)(MinWindowSizeDips * dpiScale);
        var maxSize = (int)(MaxWindowSizeDips * dpiScale);

        var desiredSize = Math.Max(currentSize.Width, currentSize.Height);
        var newSize = Math.Clamp(desiredSize, minSize, maxSize);
        if (currentSize.Width == newSize && currentSize.Height == newSize) return;

        // To prevent the window from "jumping" during resize, calculate the new top-left
        // position required to keep the window's center point stationary.
        var centerX = currentPosition.X + currentSize.Width / 2;
        var centerY = currentPosition.Y + currentSize.Height / 2;
        var newX = centerX - newSize / 2;
        var newY = centerY - newSize / 2;

        // IMPORTANT: Temporarily unsubscribe to prevent recursive loop during resize
        window.Changed -= OnAppWindowChanged;
        window.MoveAndResize(new RectInt32(newX, newY, newSize, newSize));
        window.Changed += OnAppWindowChanged;
    }

    private double GetDpiScale() => NativeMethods.GetDpiScale(WindowNative.GetWindowHandle(this));

    private void OnRestoreButtonClicked(object? sender, EventArgs e)
    {
        // Restoring closes this window, and closing during the click event can crash WinUI with E_UNEXPECTED;
        // a dispatcher enqueue alone does not avoid it, so defer the restore briefly.
        _appWindow.Hide();

        Task.Delay(50).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() => RestoreRequested?.Invoke());
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _view.RestoreButtonClicked -= OnRestoreButtonClicked;
        _appWindow.Changed -= OnAppWindowChanged;
        _themeService.ActualThemeChanged -= ApplyTheme;
    }
}
