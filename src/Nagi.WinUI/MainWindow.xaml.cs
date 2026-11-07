using Windows.Graphics;
using Windows.UI;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Settings;
using Nagi.Presentation.Models;
using Nagi.Presentation.Services;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;

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
        ((OverlappedPresenter)AppWindow.Presenter).SetBorderAndTitleBar(true, Content is not SplashPage);
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
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;

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

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (_rootElement.ActualTheme == ElementTheme.Dark)
        {
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x99, 0x99, 0x99);
        }
        else
        {
            titleBar.ButtonForegroundColor = Colors.Black;
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x20, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = Colors.Black;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x40, 0x00, 0x00, 0x00);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x66, 0x66, 0x66);
        }
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
