using System;
using Windows.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
/// A service that manages the application's visual theme, including static and dynamic theming.
/// </summary>
public class ThemeService : IThemeService {
    private readonly App _app;
    private readonly IServiceProvider _serviceProvider;
    private readonly Lazy<IUISettingsService> _settingsService;
    private readonly Lazy<IMusicPlaybackService> _playbackService;

    public ThemeService(App app, IServiceProvider serviceProvider) {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _settingsService = new Lazy<IUISettingsService>(() => _serviceProvider.GetRequiredService<IUISettingsService>());
        _playbackService = new Lazy<IMusicPlaybackService>(() => _serviceProvider.GetRequiredService<IMusicPlaybackService>());
    }

    /// <summary>
    /// Applies a specific theme (Light, Dark, or Default) to the application.
    /// </summary>
    /// <param name="theme">The theme to apply.</param>
    public void ApplyTheme(ElementTheme theme) {
        _app.ApplyThemeInternal(theme);
    }

    /// <summary>
    /// Reapplies the dynamic theme based on the currently playing track.
    /// If no track is playing, it reverts to the default accent color.
    /// </summary>
    public void ReapplyCurrentDynamicTheme() {
        var currentTrack = _playbackService.Value.CurrentTrack;
        if (currentTrack is not null) {
            ApplyDynamicThemeFromSwatches(
                currentTrack.LightSwatchId,
                currentTrack.DarkSwatchId);
        }
        else {
            ActivateDefaultPrimaryColor();
        }
    }

    /// <summary>
    /// Applies a dynamic theme based on color swatches, typically from album art.
    /// The appropriate swatch is chosen based on the application's current light/dark theme.
    /// </summary>
    /// <param name="lightSwatchId">The hex color string for the light theme.</param>
    /// <param name="darkSwatchId">The hex color string for the dark theme.</param>
    public async void ApplyDynamicThemeFromSwatches(string? lightSwatchId, string? darkSwatchId) {
        if (!await _settingsService.Value.GetDynamicThemingAsync()) {
            ActivateDefaultPrimaryColor();
            return;
        }

        if (App.RootWindow?.Content is not FrameworkElement rootElement) {
            ActivateDefaultPrimaryColor();
            return;
        }

        string? swatchToUse = rootElement.ActualTheme == ElementTheme.Dark ? darkSwatchId : lightSwatchId;

        if (!string.IsNullOrEmpty(swatchToUse) && _app.TryParseHexColor(swatchToUse, out Color targetColor)) {
            _app.SetAppPrimaryColorBrushColor(targetColor);
        }
        else {
            ActivateDefaultPrimaryColor();
        }
    }

    /// <summary>
    /// Resets the application's primary color to the default system accent color.
    /// </summary>
    public void ActivateDefaultPrimaryColor() {
        _app.SetAppPrimaryColorBrushColor(App.SystemAccentColor);
    }
}