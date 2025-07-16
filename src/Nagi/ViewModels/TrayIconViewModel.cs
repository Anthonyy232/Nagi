using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using Nagi.Services.Abstractions;
using System;
using System.Threading.Tasks;

namespace Nagi.ViewModels;

public partial class TrayIconViewModel : ObservableObject, IDisposable {
    private readonly IDispatcherService _dispatcherService;
    private readonly ISettingsService _settingsService;
    private readonly ITrayPopupService _trayPopupService;
    private readonly IWindowService _windowService;
    private readonly IAppInfoService _appInfoService;

    private bool _isDisposed;
    private bool _isHideToTrayEnabled;

    public TrayIconViewModel(
        ISettingsService settingsService,
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
        if (_windowService.IsExiting) return;

        if (_isHideToTrayEnabled) {
            args.Cancel = true;
            _dispatcherService.TryEnqueue(HideWindow);
        }
        else {
            _windowService.IsExiting = true;
        }
    }

    private void OnHideToTraySettingChanged(bool isEnabled) {
        _dispatcherService.TryEnqueue(() => {
            _isHideToTrayEnabled = isEnabled;
            UpdateTrayIconVisibility();
            if (!isEnabled && !IsWindowVisible) {
                ShowWindow();
            }
        });
    }

    [RelayCommand]
    private void ShowPopup() => _trayPopupService.ShowOrHidePopup();

    [RelayCommand]
    private void ToggleMainWindowVisibility() {
        if (!IsWindowVisible) ShowWindow();
        else if (_isHideToTrayEnabled) HideWindow();
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

    public void Dispose() {
        if (_isDisposed) return;
        _windowService.Closing -= OnAppWindowClosing;
        _windowService.VisibilityChanged -= OnAppWindowVisibilityChanged;
        _settingsService.HideToTraySettingChanged -= OnHideToTraySettingChanged;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}