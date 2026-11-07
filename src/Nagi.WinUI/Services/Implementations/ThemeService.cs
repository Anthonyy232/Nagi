using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Models;
using Nagi.Presentation.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.Core.Settings;
using Nagi.Presentation.Services;

namespace Nagi.WinUI.Services.Implementations;

public class ThemeService : IThemeService
{
    private readonly MainWindow _window;
    private readonly ILogger<ThemeService> _logger;
    private readonly IMusicPlaybackService _playbackService;
    private readonly ISettingsService _settingsService;
    private readonly IDispatcherService _dispatcherService;

    public ThemeService(MainWindow window, ISettingsService settingsService, IMusicPlaybackService playbackService,
        IDispatcherService dispatcherService, ILogger<ThemeService> logger)
    {
        _window = window;
        _settingsService = settingsService;
        _playbackService = playbackService;
        _dispatcherService = dispatcherService;
        _logger = logger;
    }

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Default;

    public void ApplyTheme(AppTheme theme)
    {
        _logger.LogDebug("Applying application theme: {Theme}", theme);
        CurrentTheme = theme;
        RunOnUIThread(() =>
        {
            if (_window.Content is not FrameworkElement rootElement) return;

            rootElement.RequestedTheme = theme.ToElementTheme();
            _ = ReapplyCurrentDynamicThemeAsync();
        });
    }

    public Task ReapplyCurrentDynamicThemeAsync()
    {
        var currentTrack = _playbackService.CurrentTrack;
        return ApplyDynamicThemeFromSwatchesAsync(currentTrack?.LightSwatchId, currentTrack?.DarkSwatchId);
    }

    public async Task ApplyDynamicThemeFromSwatchesAsync(string? lightSwatchId, string? darkSwatchId)
    {
        var isLightTheme = await IsLightThemeAsync();
        SetAppColors(ThemePalette.ForTrack(_settingsService.Get(AppSettings.DynamicTheming), lightSwatchId, darkSwatchId,
            _settingsService.Get(AppSettings.AccentColor), isLightTheme, _settingsService.Get(AppSettings.PlayerTintIntensity)));
    }

    public async Task ApplyAccentColorAsync(ArgbColor? color)
    {
        var isLightTheme = await IsLightThemeAsync();
        SetAppColors(ThemePalette.ForAccent(color, isLightTheme, _settingsService.Get(AppSettings.PlayerTintIntensity)));
    }

    private void SetAppColors(ThemePalette palette)
    {
        SetBrushColor("AppPrimaryColorBrush", palette.Primary);
        SetBrushColor("MediaOnImageAccentBrush", palette.MediaOnImageAccent);
        SetBrushColor("PlayerTintColorBrush", palette.PlayerTint);
        SetBrushColor("PlayerBackgroundBrush", palette.PlayerBackground);
        SetBrushColor("PlayerAccentBrush", palette.PlayerAccent);
    }

    /// <summary>
    ///     Updates the color of the app-level <see cref="SolidColorBrush" /> resource
    ///     <paramref name="resourceKey" />, which every bound element picks up.
    /// </summary>
    private void SetBrushColor(string resourceKey, ArgbColor color)
    {
        var newColor = color.ToWindowsColor();
        RunOnUIThread(() =>
        {
            if (Application.Current.Resources.TryGetValue(resourceKey, out var brushObject) &&
                brushObject is SolidColorBrush brush)
            {
                if (brush.Color == newColor) return;

                brush.Color = newColor;
            }
            else
            {
                _logger.LogCritical("{ResourceKey} resource not found", resourceKey);
            }
        });
    }

    private void RunOnUIThread(Action action)
    {
        if (_dispatcherService.HasThreadAccess) action();
        else _dispatcherService.TryEnqueue(action);
    }

    private async Task<bool> IsLightThemeAsync() =>
        await _dispatcherService.EnqueueAsync(() =>
            _window.Content is FrameworkElement { ActualTheme: ElementTheme.Light });
}
