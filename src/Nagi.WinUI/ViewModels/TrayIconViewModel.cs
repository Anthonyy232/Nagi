using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
/// Provides the data context and command logic for the application's system tray icon.
/// </summary>
public partial class TrayIconViewModel : ObservableObject, IDisposable {
    private readonly IDispatcherService _dispatcherService;
    private readonly IUISettingsService _settingsService;
    private readonly ITrayPopupService _trayPopupService;
    private readonly IWindowService _windowService;
    private readonly IAppInfoService _appInfoService;

    private bool _isDisposed;
    private bool _isHideToTrayEnabled;

    public TrayIconViewModel(
        IUISettingsService settingsService,
        ITrayPopupService trayPopupService,
        IWindowService windowService,
        IAppInfoService appInfoService,
        IDispatcherService dispatcherService) {
        _settingsService = settingsService;
        _trayPopupService = trayPopupService;
        _windowService = windowService;
        _appInfoService = appInfoService;
        _dispatcherService = dispatcherService;

        _settingsService.HideToTraySettingChanged += OnHideToTraySettingChanged;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the main application window is currently visible.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private bool _isWindowVisible = true;

    /// <summary>
    /// Gets or sets a value indicating whether the system tray icon should be visible.
    /// </summary>
    [ObservableProperty]
    private bool _isTrayIconVisible;

    /// <summary>
    /// Gets the tooltip text for the tray icon.
    /// </summary>
    public string ToolTipText => $"{_appInfoService.GetAppName()} - {(IsWindowVisible ? "Window Visible" : "Hidden in Tray")}";

    /// <summary>
    /// Asynchronously initializes the ViewModel by subscribing to window events and loading initial settings.
    /// </summary>
    public async Task InitializeAsync() {
        await _windowService.InitializeAsync();
        _windowService.Closing += OnAppWindowClosing;
        _windowService.VisibilityChanged += OnAppWindowVisibilityChanged;

        _isHideToTrayEnabled = await _settingsService.GetHideToTrayEnabledAsync();
        IsWindowVisible = _windowService.IsVisible;
        UpdateTrayIconVisibility();
        Debug.WriteLine($"[INFO] TrayIconViewModel: Initialized. HideToTray: {_isHideToTrayEnabled}, IsWindowVisible: {IsWindowVisible}");
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

        if (_isHideToTrayEnabled) {
            // Cancel the default close operation and hide the window to the tray instead.
            args.Cancel = true;
            Debug.WriteLine("[INFO] TrayIconViewModel: 'Hide to Tray' is enabled. Intercepting close and hiding window.");
            _dispatcherService.TryEnqueue(HideWindow);
        }
        else {
            // If not hiding to tray, closing the window means exiting the application.
            _windowService.IsExiting = true;
            Debug.WriteLine("[INFO] TrayIconViewModel: 'Hide to Tray' is disabled. Allowing application to exit.");
        }
    }

    private void OnHideToTraySettingChanged(bool isEnabled) {
        _dispatcherService.TryEnqueue(() => {
            Debug.WriteLine($"[INFO] TrayIconViewModel: 'Hide to Tray' setting changed to {isEnabled}.");
            _isHideToTrayEnabled = isEnabled;
            UpdateTrayIconVisibility();

            // Safeguard: if the feature is disabled while the window is hidden,
            // show the window to prevent it from becoming inaccessible.
            if (!isEnabled && !IsWindowVisible) {
                Debug.WriteLine("[WARN] TrayIconViewModel: 'Hide to Tray' disabled while window was hidden. Forcing window to show.");
                ShowWindow();
            }
        });
    }

    /// <summary>
    /// Shows or hides the popup menu associated with the tray icon.
    /// </summary>
    [RelayCommand]
    private void ShowPopup() => _trayPopupService.ShowOrHidePopup();

    /// <summary>
    /// Toggles the main window's visibility.
    /// </summary>
    [RelayCommand]
    private void ToggleMainWindowVisibility() {
        if (!IsWindowVisible) {
            Debug.WriteLine("[INFO] TrayIconViewModel: Executing ToggleMainWindowVisibility (Show).");
            ShowWindow();
        }
        else if (_isHideToTrayEnabled) {
            Debug.WriteLine("[INFO] TrayIconViewModel: Executing ToggleMainWindowVisibility (Minimize to Mini-Player).");
            _windowService.MinimizeToMiniPlayer();
        }
    }

    private void HideWindow() => _windowService.Hide();

    private void ShowWindow() {
        _trayPopupService.HidePopup();
        _windowService.ShowAndActivate();
    }

    /// <summary>
    /// Commands the application to exit gracefully.
    /// </summary>
    [RelayCommand]
    private void ExitApplication() {
        Debug.WriteLine("[INFO] TrayIconViewModel: Executing ExitApplication.");
        _windowService.IsExiting = true;
        _windowService.Close();
    }

    /// <summary>
    /// Cleans up resources and unsubscribes from events to prevent memory leaks.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        Debug.WriteLine("[INFO] TrayIconViewModel: Disposing and unsubscribing from events.");
        _windowService.Closing -= OnAppWindowClosing;
        _windowService.VisibilityChanged -= OnAppWindowVisibilityChanged;
        _settingsService.HideToTraySettingChanged -= OnHideToTraySettingChanged;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}