using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Nagi.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Nagi.ViewModels;

/// <summary>
/// Manages the application's system tray icon, its context menu, and visibility logic.
/// </summary>
public partial class TrayIconViewModel : ObservableObject, IDisposable {
    private readonly ISettingsService _settingsService;
    private readonly DispatcherQueue _dispatcherQueue;
    private Microsoft.UI.Windowing.AppWindow? _appWindow;
    private TaskbarIcon? _taskbarIconUiElement;
    private bool _isDisposed;
    private bool _isHideToTrayEnabledSetting;

    [ObservableProperty]
    private bool _isWindowActuallyVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private bool _isTrayIconEffectivelyVisible;

    /// <summary>
    /// Gets the tooltip text for the tray icon.
    /// </summary>
    public string ToolTipText => $"{GetAppName()} - {(IsWindowActuallyVisible ? "Window Visible" : "Hidden in Tray")}";

    public TrayIconViewModel(ISettingsService settingsService) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dispatcherQueue = App.MainDispatcherQueue ?? DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("DispatcherQueue not available.");
        _settingsService.HideToTraySettingChanged += OnHideToTraySettingChanged;
    }

    /// <summary>
    /// Initializes the view model with the application window and tray icon UI element.
    /// </summary>
    /// <param name="taskbarIconElement">The UI element for the tray icon.</param>
    public async Task InitializeAsync(TaskbarIcon taskbarIconElement) {
        _taskbarIconUiElement = taskbarIconElement ?? throw new ArgumentNullException(nameof(taskbarIconElement));

        if (App.RootWindow is null || (_appWindow = App.RootWindow.AppWindow) is null) {
            Trace.TraceError("[TrayIconViewModel] CRITICAL: AppWindow not available on Initialize.");
            return;
        }

        _appWindow.Closing += AppWindow_Closing_Handler;
        _appWindow.Changed += AppWindow_Changed_Handler;

        _isHideToTrayEnabledSetting = await _settingsService.GetHideToTrayEnabledAsync();
        IsWindowActuallyVisible = _appWindow.IsVisible;
        UpdateEffectiveTrayIconVisibility();
    }

    private void UpdateEffectiveTrayIconVisibility() {
        // The tray icon is visible only when the "hide to tray" feature is enabled
        // and the main window is currently hidden.
        IsTrayIconEffectivelyVisible = _isHideToTrayEnabledSetting && !IsWindowActuallyVisible;
    }

    partial void OnIsWindowActuallyVisibleChanged(bool value) {
        UpdateEffectiveTrayIconVisibility();
    }

    private void AppWindow_Changed_Handler(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args) {
        if (args.DidVisibilityChange) {
            _dispatcherQueue.TryEnqueue(() => IsWindowActuallyVisible = sender.IsVisible);
        }
    }

    private async void AppWindow_Closing_Handler(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args) {
        if (App.IsExiting) return;

        _isHideToTrayEnabledSetting = await _settingsService.GetHideToTrayEnabledAsync();

        if (_isHideToTrayEnabledSetting) {
            // Cancel the close operation and hide the window instead.
            args.Cancel = true;
            _dispatcherQueue.TryEnqueue(HideWindowInternal);
        }
        else {
            // Allow the application to exit normally.
            App.IsExiting = true;
        }
    }

    private void OnHideToTraySettingChanged(bool isEnabled) {
        _dispatcherQueue.TryEnqueue(() => {
            _isHideToTrayEnabledSetting = isEnabled;

            // If the feature is disabled and the window is hidden, show the window.
            if (!isEnabled && !IsWindowActuallyVisible) {
                ShowWindowInternal();
            }
            UpdateEffectiveTrayIconVisibility();
        });
    }

    /// <summary>
    /// Toggles the main window's visibility.
    /// </summary>
    [RelayCommand]
    private void ShowHideWindow() {
        if (_appWindow is null) return;

        if (IsWindowActuallyVisible) {
            // Only hide the window if the setting is enabled.
            if (_isHideToTrayEnabledSetting) {
                HideWindowInternal();
            }
        }
        else {
            ShowWindowInternal();
        }
    }

    private void HideWindowInternal() => _appWindow?.Hide();

    private void ShowWindowInternal() {
        if (_appWindow is null) return;

        _appWindow.Show(true);
        _appWindow.MoveInZOrderAtTop();
    }


    /// <summary>
    /// Exits the application gracefully.
    /// </summary>
    [RelayCommand]
    private void ExitApplication() {
        // Signal a deliberate exit to bypass the hide-to-tray logic.
        App.IsExiting = true;

        // This will trigger the standard window closing process, leading to a clean shutdown.
        App.RootWindow?.Close();
    }

    private string GetAppName() {
        try {
            return Package.Current.DisplayName;
        }
        catch {
            // Fallback name if package information is unavailable.
            return "Nagi";
        }
    }

    /// <summary>
    /// Cleans up resources, unregisters event handlers, and disposes the tray icon.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        if (_appWindow != null) {
            _appWindow.Closing -= AppWindow_Closing_Handler;
            _appWindow.Changed -= AppWindow_Changed_Handler;
        }

        _settingsService.HideToTraySettingChanged -= OnHideToTraySettingChanged;

        _taskbarIconUiElement?.Dispose();
        _taskbarIconUiElement = null;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}