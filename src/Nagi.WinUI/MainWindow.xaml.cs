using Windows.Graphics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Settings;
using Nagi.Presentation.Settings;
using Nagi.Presentation.Theme;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI;

/// <summary>
///     The main window: hosts the splash, onboarding or main shell content with its custom title bar, applies the
///     backdrop setting and remembers its size and position.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MinMainWindowWidth = 400;
    private const int MinMainWindowHeight = 300;

    public static readonly string AppIconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets/AppLogo.ico");

    private readonly ISettingsService _settingsService;
    private readonly ILogger<MainWindow> _logger;
    private SizeInt32 _lastWindowSize;
    private PointInt32 _lastWindowPosition;
    private bool _isActivatedOnce;
    private FrameworkElement? _rootElement;
    private OverlappedPresenter? _windowedPresenter;

    public MainWindow(ISettingsService settingsService, ILogger<MainWindow> logger)
    {
        _settingsService = settingsService;
        _logger = logger;

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(AppIconPath);
        RestoreWindowState();
        _lastWindowSize = AppWindow.Size;
        _lastWindowPosition = AppWindow.Position;

        AppWindow.Changed += OnWindowGeometryChanged;
        Activated += OnWindowActivated;
        _settingsService.SettingChanged += OnSettingChanged;
        SystemVisualSettings.TransparencyEffectsEnabledChanged += OnTransparencyEffectsChanged;
    }

    /// <summary>
    ///     Raised after <see cref="SetContent" /> replaced the window content.
    /// </summary>
    public event Action? ContentChanged;

    public void SetContent<TContent>(TContent content) where TContent : FrameworkElement, ICustomTitleBarProvider
    {
        if (_rootElement != null) _rootElement.ActualThemeChanged -= OnActualThemeChanged;
        Content = content;
        _rootElement = content;
        _rootElement.ActualThemeChanged += OnActualThemeChanged;
        InitializeCustomTitleBar();
        ContentChanged?.Invoke();
    }

    // Set before the presenter changes: full screen bounds arrive while the window is still overlapped.
    public bool IsFullScreen { get; private set; }

    // The same presenter back keeps the window maximized; a snapped window gets its bounds back but not the snap.
    public void SetFullScreen(bool isFullScreen)
    {
        if (isFullScreen == IsFullScreen) return;
        IsFullScreen = isFullScreen;
        if (isFullScreen)
        {
            _windowedPresenter = (OverlappedPresenter)AppWindow.Presenter;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            // Read first: leaving full screen updates them.
            var bounds = new RectInt32(_lastWindowPosition.X, _lastWindowPosition.Y, _lastWindowSize.Width,
                _lastWindowSize.Height);
            AppWindow.SetPresenter(_windowedPresenter);
            if (_windowedPresenter!.State == OverlappedPresenterState.Restored) AppWindow.MoveAndResize(bounds);
        }
    }

    public void SaveWindowState()
    {
        if (_settingsService.Get(AppSettings.RememberWindowSize))
        {
            var size = _lastWindowSize;
            _settingsService.Set(AppSettings.LastWindowSize, (size.Width, size.Height));
            _logger.LogDebug("Saving window size: {Width}x{Height}", size.Width, size.Height);
        }

        if (_settingsService.Get(AppSettings.RememberWindowPosition))
        {
            var position = _lastWindowPosition;
            _settingsService.Set(AppSettings.LastWindowPosition, (position.X, position.Y));
            _logger.LogDebug("Saving window position: ({X}, {Y})", position.X, position.Y);
        }
    }

    private void InitializeCustomTitleBar()
    {
        if (Content is not ICustomTitleBarProvider provider) return;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(provider.GetAppTitleBarElement());
        ((OverlappedPresenter)AppWindow.Presenter).SetBorderAndTitleBar(true, provider.ShowsCaptionButtons);
        UpdateTitleBarTheme();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_isActivatedOnce) return;
        _isActivatedOnce = true;

        InitializeCustomTitleBar();
        TrySetBackdrop();
    }

    private void OnWindowGeometryChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (IsFullScreen || sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;

        if (args.DidSizeChange) _lastWindowSize = sender.Size;
        if (args.DidPositionChange) _lastWindowPosition = sender.Position;
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting == AppSettings.BackdropMaterial) DispatcherQueue.TryEnqueue(TrySetBackdrop);
    }

    private void OnTransparencyEffectsChanged(bool isEnabled) => TrySetBackdrop();

    private void OnActualThemeChanged(FrameworkElement sender, object args) => UpdateTitleBarTheme();

    private void TrySetBackdrop()
    {
        try
        {
            SystemBackdrop = !SystemVisualSettings.TransparencyEffectsEnabled
                ? null
                : _settingsService.Get(AppSettings.BackdropMaterial) switch
                {
                    BackdropMaterial.Mica => new MicaBackdrop { Kind = MicaKind.Base },
                    BackdropMaterial.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                    BackdropMaterial.Acrylic => new DesktopAcrylicBackdrop(),
                    _ => null
                };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set system backdrop.");
        }
    }

    private void UpdateTitleBarTheme()
    {
        if (_rootElement is null) return;

        AppWindow.TitleBar.PreferredTheme =
            _rootElement.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    // Runs before the window is first shown, so it opens at the remembered place without a visible jump.
    private void RestoreWindowState()
    {
        try
        {
            var savedSize = _settingsService.Get(AppSettings.RememberWindowSize)
                ? _settingsService.Get(AppSettings.LastWindowSize)
                : null;
            var savedPosition = _settingsService.Get(AppSettings.RememberWindowPosition)
                ? _settingsService.Get(AppSettings.LastWindowPosition)
                : null;

            if (savedSize.HasValue && savedPosition.HasValue)
            {
                var width = Math.Max(savedSize.Value.Width, MinMainWindowWidth);
                var height = Math.Max(savedSize.Value.Height, MinMainWindowHeight);

                AppWindow.MoveAndResize(new RectInt32(
                    savedPosition.Value.X, savedPosition.Value.Y,
                    width, height));

                _logger.LogDebug("Restored window state: ({X}, {Y}) {Width}x{Height}",
                    savedPosition.Value.X, savedPosition.Value.Y, width, height);
            }
            else if (savedSize.HasValue)
            {
                var width = Math.Max(savedSize.Value.Width, MinMainWindowWidth);
                var height = Math.Max(savedSize.Value.Height, MinMainWindowHeight);

                AppWindow.Resize(new SizeInt32(width, height));
                _logger.LogDebug("Restored window size: {Width}x{Height}", width, height);
            }
            else if (savedPosition.HasValue)
            {
                AppWindow.Move(new PointInt32(savedPosition.Value.X, savedPosition.Value.Y));
                _logger.LogDebug("Restored window position: ({X}, {Y})", savedPosition.Value.X, savedPosition.Value.Y);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore window state.");
        }
    }
}
