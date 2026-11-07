using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using Nagi.Presentation.Models;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.Services;

/// <summary>
///     Re-themes the app whenever the theme, accent, dynamic theming or player design settings, the playing track or
///     the light or dark theme shown change.
/// </summary>
public sealed class ThemeCoordinator
{
    private static readonly Setting[] ThemeSettings =
    [
        AppSettings.Theme, AppSettings.AccentColor, AppSettings.DynamicTheming, AppSettings.PlayerTintIntensity,
        AppSettings.PlayerBackgroundMaterial
    ];

    private readonly IThemeService _themeService;
    private readonly ISettingsService _settingsService;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<ThemeCoordinator> _logger;

    public ThemeCoordinator(IThemeService themeService, ISettingsService settingsService,
        IMusicPlaybackService playbackService, IDispatcherService dispatcherService, ILogger<ThemeCoordinator> logger)
    {
        _themeService = themeService;
        _settingsService = settingsService;
        _playbackService = playbackService;
        _dispatcherService = dispatcherService;
        _logger = logger;
    }

    /// <summary>
    ///     Applies the saved theme and keeps it applied from now on.
    /// </summary>
    public void Start()
    {
        _settingsService.SettingChanged += OnSettingChanged;
        _playbackService.TrackChanged += Reapply;
        _themeService.ActualThemeChanged += Reapply;
        Reapply();
    }

    private void OnSettingChanged(Setting setting)
    {
        if (ThemeSettings.Contains(setting)) Reapply();
    }

    private void Reapply() => _dispatcherService.TryEnqueue(() =>
    {
        var theme = _settingsService.Get(AppSettings.Theme);
        _logger.LogDebug("Applying theme {Theme}", theme);
        _themeService.SetTheme(theme);

        var track = _playbackService.CurrentTrack;
        _themeService.SetColors(ThemePalette.ForTrack(_settingsService.Get(AppSettings.DynamicTheming),
            track?.LightSwatchId, track?.DarkSwatchId, _settingsService.Get(AppSettings.AccentColor),
            _themeService.IsLightTheme, _settingsService.Get(AppSettings.PlayerTintIntensity)));
    });
}
