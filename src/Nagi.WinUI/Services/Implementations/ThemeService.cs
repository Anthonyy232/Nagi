using System;
using System.Threading.Tasks;
using MaterialColorUtilities.ColorAppearance;
using MaterialColorUtilities.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Models;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;
using Nagi.WinUI.Helpers;
using Color = Windows.UI.Color;

namespace Nagi.WinUI.Services.Implementations;

public class ThemeService : IThemeService
{
    // Roughly 4.5:1 contrast against the dark media overlay surface (#303030).
    private const double MinimumMediaOnImageLuminance = 0.31;
    private const byte OpaqueAlpha = byte.MaxValue;
    private static readonly Color White = Color.FromArgb(OpaqueAlpha, byte.MaxValue, byte.MaxValue, byte.MaxValue);

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

        if (TryParseSwatch(swatchId, out var targetColor))
        {
            _logger.LogDebug("Applying dynamic theme using {ThemeMode} swatch: {Swatch}", actualTheme,
                swatchId);
            var mediaSourceColor = TryParseSwatch(darkSwatchId, out var darkSwatchColor)
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

    public async Task ActivateDefaultPrimaryColorAsync() => await ActivateDefaultPrimaryColorAsync(await GetActualThemeAsync());

    private async Task ActivateDefaultPrimaryColorAsync(ElementTheme actualTheme)
    {
        _logger.LogDebug("Activating configured primary color.");
        await SetAppColorsAsync(await GetConfiguredAccentColorAsync(), actualTheme);
    }

    public async Task ApplyAccentColorAsync(ArgbColor? color)
    {
        var theme = await GetActualThemeAsync();
        if (color is not null)
        {
            _logger.LogDebug("Applying manual accent color: {Color}", color);
        }

        await SetAppColorsAsync((color ?? SettingsDefaults.BrandAccentColor).ToWindowsColor(), theme);
    }

    private async Task SetAppColorsAsync(
        Color primaryColor,
        ElementTheme theme,
        Color? mediaOnImageSourceColor = null)
    {
        SetBrushColor("AppPrimaryColorBrush", primaryColor, refreshTaskbarIcons: true);

        var mediaOnImageAccentColor = GetMediaOnImageAccentColor(mediaOnImageSourceColor ?? primaryColor);
        SetBrushColor("MediaOnImageAccentBrush", mediaOnImageAccentColor);

        var intensity = await _settingsService.GetPlayerTintIntensityAsync();
        var baseChannel = theme == ElementTheme.Light ? byte.MaxValue : byte.MinValue;
        var playerTintColor = Color.FromArgb(
            OpaqueAlpha,
            BlendChannel(baseChannel, primaryColor.R, intensity),
            BlendChannel(baseChannel, primaryColor.G, intensity),
            BlendChannel(baseChannel, primaryColor.B, intensity));
        var isLight = theme == ElementTheme.Light;
        var playerBackgroundColor = ClampTone(playerTintColor, isLight ? 85 : 0, isLight ? 100 : 35);
        var playerAccentColor = ClampTone(primaryColor, isLight ? 0 : 80, isLight ? 40 : 100);
        SetBrushColor("PlayerTintColorBrush", playerTintColor);
        SetBrushColor("PlayerBackgroundBrush", playerBackgroundColor);
        SetBrushColor("PlayerAccentBrush", playerAccentColor);
    }

    /// <summary>
    ///     Updates the color of the app-level <see cref="SolidColorBrush" /> resource
    ///     <paramref name="resourceKey" />, which every bound element picks up.
    /// </summary>
    private void SetBrushColor(string resourceKey, Color newColor, bool refreshTaskbarIcons = false)
    {
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

    private static Color ClampTone(Color color, double minimum, double maximum)
    {
        var hct = Hct.FromInt(ColorUtils.ArgbFromRgb(color.R, color.G, color.B));
        var tone = Math.Clamp(hct.Tone, minimum, maximum);
        if (tone == hct.Tone) return color;

        hct.Tone = tone;
        var argb = hct.ToInt();
        return Color.FromArgb(OpaqueAlpha, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    private async Task<Color> GetConfiguredAccentColorAsync()
    {
        return (await _settingsService.GetAccentColorAsync() ?? SettingsDefaults.BrandAccentColor).ToWindowsColor();
    }

    private static bool TryParseSwatch(string? swatchId, out Color color)
    {
        var parsed = ArgbColor.TryParseHex(swatchId, out var argb);
        color = argb.ToWindowsColor();
        return parsed;
    }

    private static Color GetMediaOnImageAccentColor(Color color)
    {
        var opaqueColor = Color.FromArgb(OpaqueAlpha, color.R, color.G, color.B);
        if (GetRelativeLuminance(opaqueColor) >= MinimumMediaOnImageLuminance)
        {
            return opaqueColor;
        }

        var low = 0.0;
        var high = 1.0;

        for (var i = 0; i < 8; i++)
        {
            var amount = (low + high) / 2;
            var candidate = BlendColors(opaqueColor, White, amount);

            if (GetRelativeLuminance(candidate) >= MinimumMediaOnImageLuminance)
            {
                high = amount;
            }
            else
            {
                low = amount;
            }
        }

        return BlendColors(opaqueColor, White, high);
    }

    private static Color BlendColors(Color from, Color to, double amount)
    {
        return Color.FromArgb(
            OpaqueAlpha,
            BlendChannel(from.R, to.R, amount),
            BlendChannel(from.G, to.G, amount),
            BlendChannel(from.B, to.B, amount));
    }

    private static byte BlendChannel(byte from, byte to, double amount)
    {
        return (byte)(from + ((to - from) * amount));
    }

    private static double GetRelativeLuminance(Color color)
    {
        return ColorUtils.XyzFromArgb(ColorUtils.ArgbFromRgb(color.R, color.G, color.B))[1] / 100;
    }

    private async Task<ElementTheme> GetActualThemeAsync()
    {
        var theme = await _dispatcherService.EnqueueAsync<ElementTheme?>(() =>
            _window.Content is FrameworkElement root ? root.ActualTheme : (ElementTheme?)null);

        return theme ?? ElementTheme.Default;
    }
}
