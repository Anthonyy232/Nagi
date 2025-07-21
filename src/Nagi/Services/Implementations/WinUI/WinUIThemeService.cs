using Microsoft.UI.Xaml;
using Nagi.Services.Abstractions;
using System;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI;

namespace Nagi.Services.Implementations.WinUI;

// This service ncapsulates all dynamic theming logic.
public class WinUIThemeService : IThemeService {
    private readonly App _app;
    private readonly IServiceProvider _serviceProvider;
    private readonly Lazy<ISettingsService> _settingsService;
    private readonly Lazy<IMusicPlaybackService> _playbackService;

    public WinUIThemeService(App app, IServiceProvider serviceProvider) {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _settingsService = new Lazy<ISettingsService>(() => _serviceProvider.GetRequiredService<ISettingsService>());
        _playbackService = new Lazy<IMusicPlaybackService>(() => _serviceProvider.GetRequiredService<IMusicPlaybackService>());
    }

    public void ApplyTheme(ElementTheme theme) {
        _app.ApplyThemeInternal(theme);
    }

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

    public void ActivateDefaultPrimaryColor() {
        _app.SetAppPrimaryColorBrushColor(App.SystemAccentColor);
    }
}