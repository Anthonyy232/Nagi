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
/// ViewModel for managing the application's tray icon, including its visibility,
/// context menu commands, and interaction with the main application window.
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
    /// Gets the tooltip text for the tray icon, indicating the application name and window state.
    /// </summary>
    public string ToolTipText => $"{GetAppName()} - {(IsWindowActuallyVisible ? "Window Visible" : "Hidden in Tray")}";

    public TrayIconViewModel(ISettingsService settingsService) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dispatcherQueue = App.MainDispatcherQueue ?? DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("DispatcherQueue not available.");
        _settingsService.HideToTraySettingChanged += OnHideToTraySettingChanged;
    }

    /// <summary>
    /// Initializes the ViewModel with references to the main window and the tray icon UI element.
    /// This should be called once the main window is available.
    /// </summary>
    /// <param name="taskbarIconElement">The UI element for the tray icon.</param>
    public async Task InitializeAsync(TaskbarIcon taskbarIconElement) {
        _taskbarIconUiElement = taskbarIconElement ?? throw new ArgumentNullException(nameof(taskbarIconElement));

        if (App.RootWindow == null || (_appWindow = App.RootWindow.AppWindow) == null) {
            Debug.WriteLine("[TrayIconViewModel] CRITICAL: AppWindow not available on Initialize.");
            return;
        }

        _appWindow.Closing += AppWindow_Closing_Handler;
        _appWindow.Changed += AppWindow_Changed_Handler;

        _isHideToTrayEnabledSetting = await _settingsService.GetHideToTrayEnabledAsync();
        IsWindowActuallyVisible = _appWindow.IsVisible;

        UpdateEffectiveTrayIconVisibility();

        var startMinimized = await _settingsService.GetStartMinimizedEnabledAsync();
        if (startMinimized && IsWindowActuallyVisible) {
            _dispatcherQueue.TryEnqueue(() => {
                if (IsWindowActuallyVisible) {
                    HideWindowInternal();
                }
            });
        }
    }

    private void UpdateEffectiveTrayIconVisibility() {
        IsTrayIconEffectivelyVisible = _isHideToTrayEnabledSetting && !IsWindowActuallyVisible;
    }

    partial void OnIsWindowActuallyVisibleChanged(bool value) {
        UpdateEffectiveTrayIconVisibility();
    }

    private void AppWindow_Changed_Handler(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args) {
        if (args.DidVisibilityChange) {
            _dispatcherQueue.TryEnqueue(() => {
                IsWindowActuallyVisible = sender.IsVisible;
            });
        }
    }

    private async void AppWindow_Closing_Handler(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args) {
        if (App.IsExiting) return;

        _isHideToTrayEnabledSetting = await _settingsService.GetHideToTrayEnabledAsync();

        if (_isHideToTrayEnabledSetting) {
            args.Cancel = true;
            _dispatcherQueue.TryEnqueue(HideWindowInternal);
        }
        else {
            App.IsExiting = true;
        }
    }

    private void OnHideToTraySettingChanged(bool isEnabled) {
        _dispatcherQueue.TryEnqueue(() => {
            _isHideToTrayEnabledSetting = isEnabled;
            if (!isEnabled && !IsWindowActuallyVisible) {
                ShowWindowInternal();
            }
            UpdateEffectiveTrayIconVisibility();
        });
    }

    /// <summary>
    /// Toggles the main window's visibility between shown and hidden (in tray).
    /// </summary>
    [RelayCommand]
    private void ShowHideWindow() {
        if (_appWindow == null) return;

        if (IsWindowActuallyVisible) {
            if (_isHideToTrayEnabledSetting) {
                HideWindowInternal();
            }
        }
        else {
            ShowWindowInternal();
        }
    }

    private void HideWindowInternal() => _appWindow?.Hide();
    private void ShowWindowInternal() => _appWindow?.Show(true);

    /// <summary>
    /// Exits the application cleanly, ensuring all resources are disposed.
    /// </summary>
    [RelayCommand]
    private void ExitApplication() {
        // Signal that a deliberate exit is happening. This will bypass the
        // "hide to tray" logic in the AppWindow.Closing event handler.
        App.IsExiting = true;

        // Trigger the standard window closing process.
        // Cleanup is handled in the App.xaml.cs Window.Closed event.
        App.RootWindow?.Close();
    }

    private string GetAppName() {
        try {
            return Package.Current.DisplayName;
        }
        catch {
            return "Nagi";
        }
    }

    /// <summary>
    /// Cleans up resources used by the ViewModel, such as event handlers and the tray icon.
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