using System;
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

    public ThemeService(App app, IServiceProvider serviceProvider, ILogger<ThemeService> logger)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
        _settingsService =
            new Lazy<IUISettingsService>(() => _serviceProvider.GetRequiredService<IUISettingsService>());
        _playbackService =
            new Lazy<IMusicPlaybackService>(() => _serviceProvider.GetRequiredService<IMusicPlaybackService>());
    }

    public void ApplyTheme(ElementTheme theme)
    {
        _logger.LogInformation("Applying application theme: {Theme}", theme);
        _app.ApplyThemeInternal(theme);
    }

    public void ReapplyCurrentDynamicTheme()
    {
        _logger.LogInformation("Reapplying current dynamic theme.");
        var currentTrack = _playbackService.Value.CurrentTrack;
        if (currentTrack is not null)
        {
            ApplyDynamicThemeFromSwatches(currentTrack.LightSwatchId, currentTrack.DarkSwatchId);
        }
        else
        {
            _logger.LogDebug("No track is playing. Reverting to default primary color.");
            ActivateDefaultPrimaryColor();
        }
    }

    public async void ApplyDynamicThemeFromSwatches(string? lightSwatchId, string? darkSwatchId)
    {
        if (!await _settingsService.Value.GetDynamicThemingAsync())
        {
            _logger.LogDebug("Dynamic theming is disabled. Activating default primary color.");
            ActivateDefaultPrimaryColor();
            return;
        }

        if (App.RootWindow?.Content is not FrameworkElement rootElement)
        {
            _logger.LogWarning("Could not get root FrameworkElement. Cannot apply dynamic theme.");
            ActivateDefaultPrimaryColor();
            return;
        }

        var swatchToUse = rootElement.ActualTheme == ElementTheme.Dark ? darkSwatchId : lightSwatchId;

        if (!string.IsNullOrEmpty(swatchToUse) && _app.TryParseHexColor(swatchToUse, out var targetColor))
        {
            _logger.LogDebug("Applying dynamic theme using {ThemeMode} swatch: {Swatch}", rootElement.ActualTheme,
                swatchToUse);
            _app.SetAppPrimaryColorBrushColor(targetColor);
        }
        else
        {
            _logger.LogDebug("No valid swatch found for current theme. Activating default primary color.");
            ActivateDefaultPrimaryColor();
        }
    }

    public void ActivateDefaultPrimaryColor()
    {
        _logger.LogInformation("Activating default primary color.");
        _app.SetAppPrimaryColorBrushColor(App.SystemAccentColor);
    }
}