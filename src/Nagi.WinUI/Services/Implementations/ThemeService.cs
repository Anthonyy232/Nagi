using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Models;
using Nagi.Presentation.Services.Abstractions;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Services.Implementations;

public class ThemeService : IThemeService
{
    private readonly MainWindow _window;
    private readonly ILogger<ThemeService> _logger;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IUISettingsService _settingsService;
    private readonly IDispatcherService _dispatcherService;

    public ThemeService(MainWindow window, IUISettingsService settingsService, IMusicPlaybackService playbackService,
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
            _window.InitializeCustomTitleBar();
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
        SetAppColors(ThemePalette.ForTrack(await _settingsService.GetDynamicThemingAsync(), lightSwatchId, darkSwatchId,
            await _settingsService.GetAccentColorAsync(), isLightTheme, await _settingsService.GetPlayerTintIntensityAsync()));
    }

    public async Task ApplyAccentColorAsync(ArgbColor? color)
    {
        var isLightTheme = await IsLightThemeAsync();
        SetAppColors(ThemePalette.ForAccent(color, isLightTheme, await _settingsService.GetPlayerTintIntensityAsync()));
    }

    private void SetAppColors(ThemePalette palette)
    {
        SetBrushColor("AppPrimaryColorBrush", palette.Primary, refreshTaskbarIcons: true);
        SetBrushColor("MediaOnImageAccentBrush", palette.MediaOnImageAccent);
        SetBrushColor("PlayerTintColorBrush", palette.PlayerTint);
        SetBrushColor("PlayerBackgroundBrush", palette.PlayerBackground);
        SetBrushColor("PlayerAccentBrush", palette.PlayerAccent);
    }

    /// <summary>
    ///     Updates the color of the app-level <see cref="SolidColorBrush" /> resource
    ///     <paramref name="resourceKey" />, which every bound element picks up.
    /// </summary>
    private void SetBrushColor(string resourceKey, ArgbColor color, bool refreshTaskbarIcons = false)
    {
        var newColor = color.ToWindowsColor();
        RunOnUIThread(() =>
        {
            if (Application.Current.Resources.TryGetValue(resourceKey, out var brushObject) &&
                brushObject is SolidColorBrush brush)
            {
                if (brush.Color == newColor) return;

                brush.Color = newColor;
                if (refreshTaskbarIcons) _ = _window.TaskbarService?.RefreshIconsAsync();
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
