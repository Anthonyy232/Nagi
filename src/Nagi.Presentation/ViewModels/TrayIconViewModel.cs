using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

public partial class TrayIconViewModel : ObservableObject
{
    private readonly IAppInfoService _appInfoService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<TrayIconViewModel> _logger;
    private readonly IUISettingsService _settingsService;
    private readonly ITrayPopupService _trayPopupService;
    private readonly IWindowService _windowService;

    private bool _isHideToTrayEnabled;

    public TrayIconViewModel(
        IUISettingsService settingsService,
        ITrayPopupService trayPopupService,
        IWindowService windowService,
        IAppInfoService appInfoService,
        IDispatcherService dispatcherService,
        ILogger<TrayIconViewModel> logger)
    {
        _settingsService = settingsService;
        _trayPopupService = trayPopupService;
        _windowService = windowService;
        _appInfoService = appInfoService;
        _dispatcherService = dispatcherService;
        _logger = logger;

        _settingsService.HideToTraySettingChanged += OnHideToTraySettingChanged;
    }

    [ObservableProperty]
    public partial bool IsTrayIconVisible { get; set; }

    public string ToolTipText => _appInfoService.GetAppName();

    public void Cleanup()
    {
        _logger.LogDebug("Cleaning up TrayIconViewModel resources");
        _windowService.Closing -= OnAppWindowClosing;
        _windowService.UIStateChanged -= OnWindowStateChanged;
        _settingsService.HideToTraySettingChanged -= OnHideToTraySettingChanged;
    }


    public async Task InitializeAsync()
    {
        _windowService.Closing += OnAppWindowClosing;
        _windowService.UIStateChanged += OnWindowStateChanged;

        _isHideToTrayEnabled = await _settingsService.GetHideToTrayEnabledAsync();
        UpdateTrayIconVisibility();
        _logger.LogDebug("Initialized. HideToTray: {IsHideToTrayEnabled}, IsWindowVisible: {IsWindowVisible}",
            _isHideToTrayEnabled, _windowService.IsVisible);

    }

    private void UpdateTrayIconVisibility()
    {
        IsTrayIconVisible = _isHideToTrayEnabled && !_windowService.IsVisible;
    }

    private void OnWindowStateChanged()
    {
        _dispatcherService.TryEnqueue(UpdateTrayIconVisibility);
    }

    private void OnAppWindowClosing(WindowClosingEventArgs args)
    {
        if (_windowService.IsExiting) return;

        // Closing during onboarding exits, since there is nothing to keep running in the tray.
        if (_isHideToTrayEnabled && !_windowService.IsShowingOnboarding)
        {
            args.Cancel = true;
            _logger.LogDebug("'Hide to Tray' is enabled. Intercepting close and hiding window");
            _dispatcherService.TryEnqueue(_windowService.Hide);
        }
    }

    private void OnHideToTraySettingChanged(bool isEnabled)
    {
        _dispatcherService.TryEnqueue(() =>
        {
            _logger.LogDebug("'Hide to Tray' setting changed to {IsEnabled}", isEnabled);
            _isHideToTrayEnabled = isEnabled;
            UpdateTrayIconVisibility();

            // If the feature is disabled while the window is hidden, show the window to prevent it from becoming inaccessible.
            if (!isEnabled && !_windowService.IsVisible)
            {
                _logger.LogWarning("'Hide to Tray' disabled while window was hidden. Forcing window to show");
                RestoreWindow();
            }
        });
    }

    [RelayCommand]
    private void ShowPopup()
    {
        if (_windowService.IsVisible)
        {
            _trayPopupService.HidePopup();
            return;
        }

        _trayPopupService.ShowOrHidePopup();
    }

    /// <summary>
    ///     Restores the main application window from the system tray.
    ///     If the window is hidden, it restores and activates it.
    ///     If it is already visible (e.g., minimized to mini-player), it activates the main window.
    /// </summary>
    [RelayCommand]
    private void RestoreWindow()
    {
        _trayPopupService.HidePopup();
        _windowService.ShowAndActivate();
    }

    [RelayCommand]
    private void ExitApplication() => _windowService.Exit();
}
