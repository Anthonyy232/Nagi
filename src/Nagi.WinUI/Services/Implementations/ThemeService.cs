using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;
using Color = Windows.UI.Color;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     A service that manages the application's visual theme, including static and dynamic theming.
/// </summary>
public class ThemeService : IThemeService
{
    // Roughly 4.5:1 contrast against the dark media overlay surface (#303030).
    private const double MinimumMediaOnImageLuminance = 0.31;
    private const byte OpaqueAlpha = byte.MaxValue;
    private static readonly Color White = Color.FromArgb(OpaqueAlpha, byte.MaxValue, byte.MaxValue, byte.MaxValue);

    private readonly App _app;
    private readonly ILogger<ThemeService> _logger;
    private readonly Lazy<IMusicPlaybackService> _playbackService;
    private readonly IServiceProvider _serviceProvider;
    private readonly Lazy<IUISettingsService> _settingsService;
    private readonly Lazy<IDispatcherService> _dispatcherService;

    public ThemeService(App app, IServiceProvider serviceProvider, ILogger<ThemeService> logger)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
        _settingsService =
            new Lazy<IUISettingsService>(() => _serviceProvider.GetRequiredService<IUISettingsService>());
        _playbackService =
            new Lazy<IMusicPlaybackService>(() => _serviceProvider.GetRequiredService<IMusicPlaybackService>());
        _dispatcherService =
            new Lazy<IDispatcherService>(() => _serviceProvider.GetRequiredService<IDispatcherService>());
    }

    public ElementTheme CurrentTheme { get; private set; } = ElementTheme.Default;

    public void ApplyTheme(ElementTheme theme)
    {
        _logger.LogDebug("Applying application theme: {Theme}", theme);
        CurrentTheme = theme;
        _app.ApplyThemeInternal(theme);
    }

    public async Task ReapplyCurrentDynamicThemeAsync()
    {
        _logger.LogDebug("Reapplying current dynamic theme.");
        var currentTrack = _playbackService.Value.CurrentTrack;
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
        if (!await _settingsService.Value.GetDynamicThemingAsync())
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

    public async Task ActivateDefaultPrimaryColorAsync(ElementTheme? theme = null)
    {
        _logger.LogDebug("Activating configured primary color.");
        var actualTheme = theme ?? await GetActualThemeAsync();
        await SetAppColorsAsync(await GetConfiguredAccentColorAsync(), actualTheme);
    }

    public async Task ApplyAccentColorAsync(Color? color)
    {
        var theme = await GetActualThemeAsync();
        if (color is not null)
        {
            _logger.LogDebug("Applying manual accent color: {Color}", color);
        }

        await SetAppColorsAsync(color ?? App.DefaultAccentColor, theme);
    }

    private async Task SetAppColorsAsync(
        Color primaryColor,
        ElementTheme theme,
        Color? mediaOnImageSourceColor = null)
    {
        // 1. Set the global primary accent color (for buttons, text, etc.)
        _app.SetAppPrimaryColorBrushColor(primaryColor);

        // 2. Set the accent for controls displayed over dark cover-art surfaces.
        var mediaOnImageAccentColor = GetMediaOnImageAccentColor(mediaOnImageSourceColor ?? primaryColor);
        _app.SetMediaOnImageAccentBrushColor(mediaOnImageAccentColor);

        // 3. Calculate and set the player tint color based on intensity setting
        var intensity = await _settingsService.Value.GetPlayerTintIntensityAsync();
        var baseChannel = theme == ElementTheme.Light ? byte.MaxValue : byte.MinValue;
        var playerTintColor = Color.FromArgb(
            OpaqueAlpha,
            BlendChannel(baseChannel, primaryColor.R, intensity),
            BlendChannel(baseChannel, primaryColor.G, intensity),
            BlendChannel(baseChannel, primaryColor.B, intensity));
        _app.SetPlayerTintColorBrushColor(playerTintColor);
    }

    private async Task<Color> GetConfiguredAccentColorAsync()
    {
        return await _settingsService.Value.GetAccentColorAsync() ?? App.DefaultAccentColor;
    }

    private bool TryParseSwatch(string? swatchId, out Color color)
    {
        color = default;
        return !string.IsNullOrWhiteSpace(swatchId) && _app.TryParseHexColor(swatchId, out color);
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
        return (0.2126 * GetLinearChannelValue(color.R)) +
               (0.7152 * GetLinearChannelValue(color.G)) +
               (0.0722 * GetLinearChannelValue(color.B));
    }

    private static double GetLinearChannelValue(byte value)
    {
        var channel = value / 255d;
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private async Task<ElementTheme> GetActualThemeAsync()
    {
        var theme = await _dispatcherService.Value.EnqueueAsync<ElementTheme?>(() =>
            App.RootWindow?.Content is FrameworkElement root ? root.ActualTheme : (ElementTheme?)null);

        return theme ?? ElementTheme.Default;
    }
}
