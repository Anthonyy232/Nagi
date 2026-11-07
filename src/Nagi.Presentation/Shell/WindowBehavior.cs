using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Settings;

namespace Nagi.Presentation.Shell;

/// <summary>
///     What the main window does when it is closed, minimized or shown at launch: hide to the tray, give way to the
///     mini player or start minimized, as the settings ask. Onboarding keeps the ordinary behaviour, since nothing
///     can be playing yet.
/// </summary>
public sealed class WindowBehavior
{
    private readonly IWindowService _windowService;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ISettingsService _settingsService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<WindowBehavior> _logger;

    public WindowBehavior(IWindowService windowService, IApplicationLifecycle applicationLifecycle,
        ISettingsService settingsService, IDispatcherService dispatcherService, ILogger<WindowBehavior> logger)
    {
        _windowService = windowService;
        _applicationLifecycle = applicationLifecycle;
        _settingsService = settingsService;
        _dispatcherService = dispatcherService;
        _logger = logger;

        _windowService.Closing += OnClosing;
        _windowService.Minimized += OnMinimized;
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
