using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using Nagi.Presentation.Settings;

namespace Nagi.Presentation.Shell;

/// <summary>
///     What the main window does when it is closed, minimized or shown at launch: hide to the tray, give way to the
///     mini player or start minimized, as the settings ask. Onboarding keeps the ordinary behaviour, since nothing
///     can be playing yet. While nothing plays and the main window is out of sight, the app runs in efficiency mode.
/// </summary>
public sealed class WindowBehavior
{
    private readonly IWindowService _windowService;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ISettingsService _settingsService;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<WindowBehavior> _logger;

    public WindowBehavior(IWindowService windowService, IApplicationLifecycle applicationLifecycle,
        ISettingsService settingsService, IMusicPlaybackService playbackService, IDispatcherService dispatcherService,
        ILogger<WindowBehavior> logger)
    {
        _windowService = windowService;
        _applicationLifecycle = applicationLifecycle;
        _settingsService = settingsService;
        _playbackService = playbackService;
        _dispatcherService = dispatcherService;
        _logger = logger;

        _windowService.Closing += OnClosing;
        _windowService.Minimized += OnMinimized;
        _windowService.UIStateChanged += OnUIStateChanged;
        _playbackService.PlaybackStateChanged += OnUIStateChanged;
    }

    private bool StartMinimized => _settingsService.Get(AppSettings.StartMinimized);
    private bool HideToTray => _settingsService.Get(AppSettings.HideToTray);
    private bool MinimizeToMiniPlayer => _settingsService.Get(AppSettings.MinimizeToMiniPlayer);

    /// <summary>
    ///     Shows the main window while the app loads, unless it starts minimized.
    /// </summary>
    public void ShowWhileLoading()
    {
        if (!StartMinimized) _windowService.ShowAndActivate();
    }

    /// <summary>
    ///     Shows the main window once the app has loaded, unless it starts minimized and no other launch asked for it.
    ///     Starting minimized opens the mini player, a minimized window or only the tray icon.
    /// </summary>
    public void ShowWhenLoaded(bool activatedExternally)
    {
        if (activatedExternally || !StartMinimized) _windowService.ShowAndActivate();
        else if (MinimizeToMiniPlayer) _windowService.ShowMiniPlayer();
        else if (!HideToTray) _windowService.ShowMinimized();
        UpdateEfficiencyMode();
    }

    private void OnUIStateChanged()
    {
        if (_dispatcherService.HasThreadAccess) UpdateEfficiencyMode();
        else _dispatcherService.TryEnqueue(UpdateEfficiencyMode);
    }

    private void UpdateEfficiencyMode()
    {
        var isOutOfSight = !_windowService.IsVisible || _windowService.IsMiniPlayerActive || _windowService.IsMinimized;
        _windowService.SetEfficiencyMode(isOutOfSight && !_playbackService.IsPlaying);
    }

    private void OnMinimized()
    {
        if (MinimizeToMiniPlayer && !_applicationLifecycle.IsShowingOnboarding) _windowService.ShowMiniPlayer();
    }

    private void OnClosing(WindowClosingEventArgs args)
    {
        // Closing during onboarding exits, since there is nothing to keep running in the tray.
        if (_windowService.IsExiting || !HideToTray || _applicationLifecycle.IsShowingOnboarding) return;

        args.Cancel = true;
        _logger.LogDebug("'Hide to Tray' is enabled. Intercepting close and hiding window");
        _dispatcherService.TryEnqueue(_windowService.Hide);
    }
}
