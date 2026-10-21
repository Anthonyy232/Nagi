using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Models;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;
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
        _window = window ?? throw new ArgumentNullException(nameof(window));
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

    public async Task ReapplyCurrentDynamicThemeAsync()
    {
        _logger.LogDebug("Reapplying current dynamic theme.");
        var currentTrack = _playbackService.CurrentTrack;
        if (currentTrack is not null)
        {
            await ApplyDynamicThemeFromSwatchesAsync(currentTrack.LightSwatchId, currentTrack.DarkSwatchId);
        }
        else
        {
            _logger.LogDebug("No track is playing. Reverting to default primary color.");
            await ActivateDefaultPrimaryColorAsync();
        }
    }

    public async Task ApplyDynamicThemeFromSwatchesAsync(string? lightSwatchId, string? darkSwatchId)
    {
        if (!await _settingsService.GetDynamicThemingAsync())
        {
            _logger.LogDebug("Dynamic theming is disabled. Activating default primary color.");
            await ActivateDefaultPrimaryColorAsync();
            return;
        }

        var actualTheme = await GetActualThemeAsync();

        var swatchId = actualTheme == ElementTheme.Dark ? darkSwatchId : lightSwatchId;

        if (ArgbColor.TryParseHex(swatchId, out var targetColor))
        {
            _logger.LogDebug("Applying dynamic theme using {ThemeMode} swatch: {Swatch}", actualTheme,
                swatchId);
            var mediaSourceColor = ArgbColor.TryParseHex(darkSwatchId, out var darkSwatchColor)
                ? darkSwatchColor
                : targetColor;

            await SetAppColorsAsync(targetColor, actualTheme, mediaSourceColor);
        }
        else
        {
            _logger.LogDebug("No valid swatch found for current theme. Activating default primary color.");
            await ActivateDefaultPrimaryColorAsync(actualTheme);
        }
    }

    private async Task ActivateDefaultPrimaryColorAsync(ElementTheme? theme = null)
    {
        _logger.LogDebug("Activating configured primary color.");
        var accentColor = await _settingsService.GetAccentColorAsync() ?? SettingsDefaults.BrandAccentColor;
        await SetAppColorsAsync(accentColor, theme ?? await GetActualThemeAsync());
    }

    public async Task ApplyAccentColorAsync(ArgbColor? color)
    {
        var theme = await GetActualThemeAsync();
        if (color is not null)
        {
            _logger.LogDebug("Applying manual accent color: {Color}", color);
        }

        await SetAppColorsAsync(color ?? SettingsDefaults.BrandAccentColor, theme);
    }

    private async Task SetAppColorsAsync(ArgbColor primaryColor, ElementTheme theme,
        ArgbColor? mediaOnImageSourceColor = null)
    {
        var palette = ThemePalette.Create(primaryColor, theme == ElementTheme.Light,
            await _settingsService.GetPlayerTintIntensityAsync(), mediaOnImageSourceColor);

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

    private async Task<ElementTheme> GetActualThemeAsync()
    {
        var theme = await _dispatcherService.EnqueueAsync<ElementTheme?>(() =>
            _window.Content is FrameworkElement root ? root.ActualTheme : (ElementTheme?)null);

        return theme ?? ElementTheme.Default;
    }
}
