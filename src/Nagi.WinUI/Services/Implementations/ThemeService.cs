using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Presentation.Theme;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Services.Implementations;

public sealed class ThemeService : IThemeService
{
    private readonly MainWindow _window;
    private readonly ILogger<ThemeService> _logger;
    private AppTheme _theme = AppTheme.Default;
    private FrameworkElement? _root;
    private bool _isLightTheme;

    public ThemeService(MainWindow window, ILogger<ThemeService> logger)
    {
        _window = window;
        _logger = logger;
        _window.ContentChanged += OnContentChanged;
        OnContentChanged();
    }

    public bool IsLightTheme => _root?.ActualTheme == ElementTheme.Light;

    public event Action? ActualThemeChanged;

    public void SetTheme(AppTheme theme)
    {
        _theme = theme;
        if (_root is not null) _root.RequestedTheme = theme.ToElementTheme();
    }

    public void SetColors(ThemePalette palette)
    {
        SetBrushColor("AppPrimaryColorBrush", palette.Primary);
        SetBrushColor("MediaOnImageAccentBrush", palette.MediaOnImageAccent);
        SetBrushColor("PlayerTintColorBrush", palette.PlayerTint);
        SetBrushColor("PlayerBackgroundBrush", palette.PlayerBackground);
        SetBrushColor("PlayerAccentBrush", palette.PlayerAccent);
    }

    // Each new root takes the current theme, and the theme shown can change with it.
    private void OnContentChanged()
    {
        if (_root is not null) _root.ActualThemeChanged -= OnRootActualThemeChanged;
        _root = _window.Content as FrameworkElement;
        if (_root is null) return;

        _root.RequestedTheme = _theme.ToElementTheme();
        _root.ActualThemeChanged += OnRootActualThemeChanged;
        OnRootActualThemeChanged(_root, null);
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object? args)
    {
        if (IsLightTheme == _isLightTheme) return;
        _isLightTheme = IsLightTheme;
        ActualThemeChanged?.Invoke();
    }

    /// <summary>
    ///     Updates the color of the app-level <see cref="SolidColorBrush" /> resource <paramref name="resourceKey" />,
    ///     which every window picks up.
    /// </summary>
    private void SetBrushColor(string resourceKey, ArgbColor color)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out var brushObject) &&
            brushObject is SolidColorBrush brush)
        {
            var newColor = color.ToWindowsColor();
            if (brush.Color != newColor) brush.Color = newColor;
        }
        else
        {
            _logger.LogCritical("{ResourceKey} resource not found", resourceKey);
        }
    }
}
