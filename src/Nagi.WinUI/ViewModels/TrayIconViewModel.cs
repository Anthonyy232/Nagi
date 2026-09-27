using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Provides the data context and command logic for the application's system tray icon.
/// </summary>
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

    /// <summary>
    ///     Gets the tooltip text for the tray icon.
    /// </summary>
    public string ToolTipText => _appInfoService.GetAppName();

    public void Cleanup()
    {
        _logger.LogDebug("Cleaning up TrayIconViewModel resources");
        _windowService.Closing -= OnAppWindowClosing;
        _windowService.VisibilityChanged -= OnAppWindowVisibilityChanged;
        _settingsService.HideToTraySettingChanged -= OnHideToTraySettingChanged;
    }


    /// <summary>
    ///     Asynchronously initializes the ViewModel by subscribing to window events and loading initial settings.
    /// </summary>
    public async Task InitializeAsync()
    {
        _windowService.Closing += OnAppWindowClosing;
        _windowService.VisibilityChanged += OnAppWindowVisibilityChanged;

        _isHideToTrayEnabled = await _settingsService.GetHideToTrayEnabledAsync();
        UpdateTrayIconVisibility();
        _logger.LogDebug("Initialized. HideToTray: {IsHideToTrayEnabled}, IsWindowVisible: {IsWindowVisible}",
            _isHideToTrayEnabled, _windowService.IsVisible);

    }

    private void UpdateTrayIconVisibility()
    {
        IsTrayIconVisible = _isHideToTrayEnabled && !_windowService.IsVisible;
    }

    private void OnAppWindowVisibilityChanged(AppWindowChangedEventArgs args)
    {
        _dispatcherService.TryEnqueue(UpdateTrayIconVisibility);
    }

    private void OnAppWindowClosing(AppWindowClosingEventArgs args)
    {
        // If the application is exiting intentionally, do not intercept the close.
        if (_windowService.IsExiting) return;

        if (App.RootWindow?.Content is OnboardingPage)
        {
            _windowService.IsExiting = true;
            _logger.LogDebug("OnboardingPage is active. Allowing application to exit");
            return;
        }

        if (_isHideToTrayEnabled)
        {
            // Cancel the default close operation and hide the window to the tray instead.
            args.Cancel = true;
            _logger.LogDebug("'Hide to Tray' is enabled. Intercepting close and hiding window");
            _dispatcherService.TryEnqueue(_windowService.Hide);
        }
        else
        {
            // If not hiding to tray, closing the window means exiting the application.
            _windowService.IsExiting = true;
            _logger.LogDebug("'Hide to Tray' is disabled. Allowing application to exit");
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

    /// <summary>
    ///     Shows or hides the popup menu associated with the tray icon.
    /// </summary>
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

    /// <summary>
    ///     Commands the application to exit gracefully.
    /// </summary>
    [RelayCommand]
    private void ExitApplication()
    {
        _windowService.IsExiting = true;
        _windowService.Close();
    }
}
