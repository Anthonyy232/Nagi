using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     A service that manages the application's visual theme, including static and dynamic theming.
/// </summary>
public class ThemeService : IThemeService
{
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

        var swatchToUse = actualTheme == ElementTheme.Dark ? darkSwatchId : lightSwatchId;

        if (!string.IsNullOrEmpty(swatchToUse) && _app.TryParseHexColor(swatchToUse, out var targetColor))
        {
            _logger.LogDebug("Applying dynamic theme using {ThemeMode} swatch: {Swatch}", actualTheme,
                swatchToUse);
            await SetAppColorsAsync(targetColor, actualTheme);
        }
        else
        {
            _logger.LogDebug("No valid swatch found for current theme. Activating default primary color.");
            await ActivateDefaultPrimaryColorAsync(actualTheme);
        }
    }

    public async Task ActivateDefaultPrimaryColorAsync(ElementTheme? theme = null)
    {
        _logger.LogDebug("Activating default primary color.");
        var actualTheme = theme ?? await GetActualThemeAsync();
        var accentColor = await _settingsService.Value.GetAccentColorAsync();
        if (accentColor != null)
        {
            await SetAppColorsAsync(accentColor.Value, actualTheme);
        }
        else
        {
            await SetAppColorsAsync(App.SystemAccentColor, actualTheme);
        }
    }

    public async Task ApplyAccentColorAsync(Windows.UI.Color? color)
    {
        var theme = await GetActualThemeAsync();
        if (color == null)
        {
            await ActivateDefaultPrimaryColorAsync();
        }
        else
        {
            _logger.LogDebug("Applying manual accent color: {Color}", color);
            await SetAppColorsAsync(color.Value, theme);
        }
    }

    private async Task SetAppColorsAsync(Windows.UI.Color primaryColor, ElementTheme theme)
    {
        // 1. Set the global primary accent color (for buttons, text, etc.)
        _app.SetAppPrimaryColorBrushColor(primaryColor);
        
        // 2. Calculate and set the player tint color based on intensity setting
        var intensity = await _settingsService.Value.GetPlayerTintIntensityAsync();
        
        // Lerp functionality: Target = Color * Intensity + (Base) * (1 - Intensity)
        // For Dark theme, Base is Black (0,0,0)
        // For Light theme, Base is White (255,255,255)
        
        byte r, g, b;
        if (theme == ElementTheme.Light)
        {
            r = (byte)((primaryColor.R * intensity) + (255 * (1 - intensity)));
            g = (byte)((primaryColor.G * intensity) + (255 * (1 - intensity)));
            b = (byte)((primaryColor.B * intensity) + (255 * (1 - intensity)));
        }
        else
        {
            r = (byte)(primaryColor.R * intensity);
            g = (byte)(primaryColor.G * intensity);
            b = (byte)(primaryColor.B * intensity);
        }

        var playerTintColor = Windows.UI.Color.FromArgb(255, r, g, b);
        _app.SetPlayerTintColorBrushColor(playerTintColor);
    }

    private async Task<ElementTheme> GetActualThemeAsync()
    {
        var theme = await _dispatcherService.Value.EnqueueAsync<ElementTheme?>(() =>
            App.RootWindow?.Content is FrameworkElement root ? root.ActualTheme : (ElementTheme?)null);

        return theme ?? ElementTheme.Default;
    }
}
