using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using System;
using System.Threading.Tasks;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
/// Provides the data and commands for the application's system tray icon.
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private bool _isWindowVisible = true;

    [ObservableProperty]
    private bool _isTrayIconVisible;

    public string ToolTipText => $"{_appInfoService.GetAppName()} - {(IsWindowVisible ? "Window Visible" : "Hidden in Tray")}";

    public async Task InitializeAsync() {
        _windowService.Closing += OnAppWindowClosing;
        _windowService.VisibilityChanged += OnAppWindowVisibilityChanged;

        _isHideToTrayEnabled = await _settingsService.GetHideToTrayEnabledAsync();
        IsWindowVisible = _windowService.IsVisible;
        UpdateTrayIconVisibility();
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
        // If the user is intentionally exiting (e.g., via the tray menu), do not intercept the close.
        if (_windowService.IsExiting) return;

        if (_isHideToTrayEnabled) {
            // Cancel the close operation and hide the window instead.
            args.Cancel = true;
            _dispatcherService.TryEnqueue(HideWindow);
        }
        else {
            // Allow the application to exit normally.
            _windowService.IsExiting = true;
        }
    }

    private void OnHideToTraySettingChanged(bool isEnabled) {
        _dispatcherService.TryEnqueue(() => {
            _isHideToTrayEnabled = isEnabled;
            UpdateTrayIconVisibility();

            // If the "Hide to Tray" feature is disabled while the window is currently hidden,
            // we must show the window to prevent it from becoming inaccessible.
            if (!isEnabled && !IsWindowVisible) {
                ShowWindow();
            }
        });
    }

    [RelayCommand]
    private void ShowPopup() => _trayPopupService.ShowOrHidePopup();

    [RelayCommand]
    private void ToggleMainWindowVisibility() {
        if (!IsWindowVisible) {
            ShowWindow();
        }
        else if (_isHideToTrayEnabled) {
            HideWindow();
        }
    }

    private void HideWindow() => _windowService.Hide();

    private void ShowWindow() {
        _trayPopupService.HidePopup();
        _windowService.ShowAndActivate();
    }

    [RelayCommand]
    private void ExitApplication() {
        _windowService.IsExiting = true;
        _windowService.Close();
    }

    /// <summary>
    /// Cleans up resources and unsubscribes from events to prevent memory leaks.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        _windowService.Closing -= OnAppWindowClosing;
        _windowService.VisibilityChanged -= OnAppWindowVisibilityChanged;
        _settingsService.HideToTraySettingChanged -= OnHideToTraySettingChanged;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}