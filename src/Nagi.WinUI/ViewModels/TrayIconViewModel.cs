using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using System;
using System.Threading.Tasks;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Provides the data context and command logic for the application's system tray icon.
/// </summary>
public partial class TrayIconViewModel : ObservableObject, IDisposable {
    private readonly IAppInfoService _appInfoService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<TrayIconViewModel> _logger;
    private readonly IUISettingsService _settingsService;
    private readonly ITrayPopupService _trayPopupService;
    private readonly IWindowService _windowService;

    private bool _isDisposed;
    private bool _isHideToTrayEnabled;

    public TrayIconViewModel(
        IUISettingsService settingsService,
        ITrayPopupService trayPopupService,
        IWindowService windowService,
        IAppInfoService appInfoService,
        IDispatcherService dispatcherService,
        ILogger<TrayIconViewModel> logger) {
        _settingsService = settingsService;
        _trayPopupService = trayPopupService;
        _windowService = windowService;
        _appInfoService = appInfoService;
        _dispatcherService = dispatcherService;
        _logger = logger;

        _settingsService.HideToTraySettingChanged += OnHideToTraySettingChanged;
    }

    /// <summary>
    ///     Gets or sets a value indicating whether the main application window is currently visible.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    public partial bool IsWindowVisible { get; set; } = true;

    /// <summary>
    ///     Gets or sets a value indicating whether the system tray icon should be visible.
    /// </summary>
    [ObservableProperty]
    public partial bool IsTrayIconVisible { get; set; }

    /// <summary>
    ///     Gets the tooltip text for the tray icon.
    /// </summary>
    public string ToolTipText =>
        $"{_appInfoService.GetAppName()} - {(IsWindowVisible ? "Window Visible" : "Hidden in Tray")}";

    /// <summary>
    ///     Cleans up resources and unsubscribes from events to prevent memory leaks.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        _logger.LogInformation("Disposing and unsubscribing from events");
        _windowService.Closing -= OnAppWindowClosing;
        _windowService.VisibilityChanged -= OnAppWindowVisibilityChanged;
        _settingsService.HideToTraySettingChanged -= OnHideToTraySettingChanged;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Asynchronously initializes the ViewModel by subscribing to window events and loading initial settings.
    /// </summary>
    public async Task InitializeAsync() {
        await _windowService.InitializeAsync();
        _windowService.Closing += OnAppWindowClosing;
        _windowService.VisibilityChanged += OnAppWindowVisibilityChanged;

        _isHideToTrayEnabled = await _settingsService.GetHideToTrayEnabledAsync();
        IsWindowVisible = _windowService.IsVisible;
        UpdateTrayIconVisibility();
        _logger.LogInformation("Initialized. HideToTray: {IsHideToTrayEnabled}, IsWindowVisible: {IsWindowVisible}",
            _isHideToTrayEnabled, IsWindowVisible);
    }

    private void UpdateTrayIconVisibility() {
        IsTrayIconVisible = _isHideToTrayEnabled && !IsWindowVisible;
    }

    private void OnAppWindowVisibilityChanged(AppWindowChangedEventArgs args) {
        _dispatcherService.TryEnqueue(() => {
            IsWindowVisible = _windowService.IsVisible;
            UpdateTrayIconVisibility();
        });
    }

    private void OnAppWindowClosing(AppWindowClosingEventArgs args) {
        // If the application is exiting intentionally, do not intercept the close.
        if (_windowService.IsExiting) return;

        // Check if the current page is the OnboardingPage - if so, always exit the app
        if (App.RootWindow?.Content is OnboardingPage) {
            _windowService.IsExiting = true;
            _logger.LogInformation("OnboardingPage is active. Allowing application to exit");
            return;
        }

        if (_isHideToTrayEnabled) {
            // Cancel the default close operation and hide the window to the tray instead.
            args.Cancel = true;
            _logger.LogInformation("'Hide to Tray' is enabled. Intercepting close and hiding window");
            _dispatcherService.TryEnqueue(HideWindow);
        }
        else {
            // If not hiding to tray, closing the window means exiting the application.
            _windowService.IsExiting = true;
            _logger.LogInformation("'Hide to Tray' is disabled. Allowing application to exit");
        }
    }

    private void OnHideToTraySettingChanged(bool isEnabled) {
        _dispatcherService.TryEnqueue(() => {
            _logger.LogInformation("'Hide to Tray' setting changed to {IsEnabled}", isEnabled);
            _isHideToTrayEnabled = isEnabled;
            UpdateTrayIconVisibility();

            // If the feature is disabled while the window is hidden, show the window to prevent it from becoming inaccessible.
            if (!isEnabled && !IsWindowVisible) {
                _logger.LogWarning("'Hide to Tray' disabled while window was hidden. Forcing window to show");
                ShowWindow();
            }
        });
    }

    /// <summary>
    ///     Shows or hides the popup menu associated with the tray icon.
    /// </summary>
    [RelayCommand]
    private void ShowPopup() {
        _trayPopupService.ShowOrHidePopup();
    }

    /// <summary>
    ///     Toggles the main window's visibility.
    /// </summary>
    [RelayCommand]
    private void ToggleMainWindowVisibility() {
        if (!IsWindowVisible) {
            ShowWindow();
        }
        else if (_isHideToTrayEnabled) {
            _windowService.MinimizeToMiniPlayer();
        }
    }

    private void HideWindow() {
        _windowService.Hide();
    }

    private void ShowWindow() {
        _trayPopupService.HidePopup();
        _windowService.ShowAndActivate();
    }

    /// <summary>
    ///     Commands the application to exit gracefully.
    /// </summary>
    [RelayCommand]
    private void ExitApplication() {
        _windowService.IsExiting = true;
        _windowService.Close();
    }
}