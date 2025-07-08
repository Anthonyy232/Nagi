using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Nagi.Helpers;
using Nagi.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Nagi.ViewModels;

/// <summary>
/// Manages the state and interactions for the application's system tray icon.
/// This includes handling window visibility, context menu commands, and "hide to tray" functionality.
/// </summary>
public partial class TrayIconViewModel : ObservableObject, IDisposable {
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ISettingsService _settingsService;
    private readonly ITrayPopupService _trayPopupService;
    private readonly IWin32InteropService _win32InteropService;
    private AppWindow? _appWindow;
    private bool _isDisposed;
    private bool _isHideToTrayEnabled;

    public TrayIconViewModel(
        ISettingsService settingsService,
        ITrayPopupService trayPopupService,
        IWin32InteropService win32InteropService) {
        _settingsService = settingsService;
        _trayPopupService = trayPopupService;
        _win32InteropService = win32InteropService;

        // This ViewModel is tightly coupled to the main UI thread.
        _dispatcherQueue = App.MainDispatcherQueue
            ?? throw new InvalidOperationException("Main thread DispatcherQueue is not available.");

        _settingsService.HideToTraySettingChanged += OnHideToTraySettingChanged;
    }

    /// <summary>
    /// Gets a value indicating whether the main application window is currently visible.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    public partial bool IsWindowVisible { get; set; } = true;

    /// <summary>
    /// Gets a value indicating whether the tray icon should be visible.
    /// This is controlled by the "Hide to Tray" user setting.
    /// </summary>
    [ObservableProperty]
    public partial bool IsTrayIconVisible { get; set; }

    /// <summary>
    /// Gets the text displayed when the user hovers over the tray icon.
    /// </summary>
    public string ToolTipText => $"{GetAppName()} - {(IsWindowVisible ? "Window Visible" : "Hidden in Tray")}";

    /// <summary>
    /// Initializes the ViewModel by attaching to the main application window events.
    /// </summary>
    public async Task InitializeAsync() {
        if (App.RootWindow?.AppWindow is not AppWindow appWindow) {
            Trace.TraceError("[TrayIconViewModel] CRITICAL: AppWindow not available on Initialize.");
            return;
        }

        _appWindow = appWindow;
        _appWindow.Closing += OnAppWindowClosing;
        _appWindow.Changed += OnAppWindowChanged;

        _isHideToTrayEnabled = await _settingsService.GetHideToTrayEnabledAsync();
        IsWindowVisible = _appWindow.IsVisible;
        UpdateTrayIconVisibility();
    }

    private void UpdateTrayIconVisibility() {
        IsTrayIconVisible = _isHideToTrayEnabled && !IsWindowVisible;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) {
        if (args.DidVisibilityChange) {
            // Ensure property updates are on the UI thread.
            _dispatcherQueue.TryEnqueue(() => {
                IsWindowVisible = sender.IsVisible;
                UpdateTrayIconVisibility();
            });
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args) {
        // If the application is exiting intentionally, allow the window to close.
        if (App.IsExiting) {
            return;
        }

        // If "Hide to Tray" is enabled, intercept the close request and hide the window instead.
        if (_isHideToTrayEnabled) {
            args.Cancel = true;
            _dispatcherQueue.TryEnqueue(HideWindow);
        }
        else {
            // Otherwise, closing the window signifies exiting the application.
            App.IsExiting = true;
        }
    }

    private void OnHideToTraySettingChanged(bool isEnabled) {
        _dispatcherQueue.TryEnqueue(() => {
            _isHideToTrayEnabled = isEnabled;
            UpdateTrayIconVisibility();

            // For better user experience, if the setting is disabled while the window
            // is hidden, show the window.
            if (!isEnabled && !IsWindowVisible) {
                ShowWindow();
            }
        });
    }

    /// <summary>
    /// Shows or hides the tray icon's associated popup.
    /// </summary>
    [RelayCommand]
    private void ShowPopup() {
        _trayPopupService.ShowOrHidePopup();
    }

    /// <summary>
    /// Toggles the main window's visibility. Hiding is only permitted
    /// if the "Hide to Tray" setting is enabled.
    /// </summary>
    [RelayCommand]
    private void ToggleMainWindowVisibility() {
        if (!IsWindowVisible) {
            ShowWindow();
        }
        else if (_isHideToTrayEnabled) {
            HideWindow();
        }
    }

    private void HideWindow() => _appWindow?.Hide();

    private void ShowWindow() {
        if (App.RootWindow is null) {
            return;
        }

        // *** FIX: Explicitly hide the popup before showing the main window. ***
        // This provides a more reliable and cleaner user experience than
        // relying solely on the popup's deactivation event.
        _trayPopupService.HidePopup();

        // Use the activator to ensure the window is brought to the foreground correctly.
        WindowActivator.ShowAndActivate(App.RootWindow, _win32InteropService);
    }

    /// <summary>
    /// Initiates a graceful shutdown of the application.
    /// </summary>
    [RelayCommand]
    private void ExitApplication() {
        App.IsExiting = true;
        App.RootWindow?.Close();
    }

    private static string GetAppName() {
        try {
            return Package.Current.DisplayName;
        }
        catch (InvalidOperationException) {
            // This can happen if the app is running unpackaged.
            return "Nagi";
        }
    }

    public void Dispose() {
        if (_isDisposed) {
            return;
        }

        if (_appWindow != null) {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
        }
        _settingsService.HideToTraySettingChanged -= OnHideToTraySettingChanged;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}