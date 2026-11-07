using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Settings;

namespace Nagi.Presentation.ViewModels;

public partial class TrayIconViewModel : ObservableObject
{
    private readonly IAppInfoService _appInfoService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<TrayIconViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly ITrayPopupService _trayPopupService;
    private readonly IWindowService _windowService;


    public TrayIconViewModel(
        ISettingsService settingsService,
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

        _settingsService.SettingChanged += OnSettingChanged;
    }

    [ObservableProperty]
    public partial bool IsTrayIconVisible { get; set; }

    public string ToolTipText => _appInfoService.GetAppName();

    public void Cleanup()
    {
        _logger.LogDebug("Cleaning up TrayIconViewModel resources");
        _windowService.UIStateChanged -= OnWindowStateChanged;
        _settingsService.SettingChanged -= OnSettingChanged;
    }

    public void Initialize()
    {
        _windowService.UIStateChanged += OnWindowStateChanged;
        UpdateTrayIconVisibility();
        _logger.LogDebug("Initialized. HideToTray: {IsHideToTrayEnabled}, IsWindowVisible: {IsWindowVisible}",
            IsHideToTrayEnabled, _windowService.IsVisible);
    }

    private bool IsHideToTrayEnabled => _settingsService.Get(AppSettings.HideToTray);

    private void UpdateTrayIconVisibility()
    {
        IsTrayIconVisible = IsHideToTrayEnabled && !_windowService.IsVisible;
    }

    private void OnWindowStateChanged()
    {
        _dispatcherService.TryEnqueue(UpdateTrayIconVisibility);
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting != AppSettings.HideToTray) return;
        _dispatcherService.TryEnqueue(() =>
        {
            var isEnabled = IsHideToTrayEnabled;
            _logger.LogDebug("'Hide to Tray' setting changed to {IsEnabled}", isEnabled);
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

        _trayPopupService.TogglePopup();
    }

    [RelayCommand]
    private void RestoreWindow()
    {
        _trayPopupService.HidePopup();
        _windowService.ShowAndActivate();
    }

    [RelayCommand]
    private void ExitApplication() => _windowService.Exit();
}
