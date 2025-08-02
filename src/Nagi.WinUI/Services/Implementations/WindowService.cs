// Nagi.WinUI.Services.Implementations/WindowService.cs

using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
/// Manages the state and interactions of the application's main window and the secondary mini-player window.
/// </summary>
public sealed class WindowService : IWindowService, IDisposable {
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IWin32InteropService _win32InteropService;
    private readonly IUISettingsService _settingsService;
    private readonly IDispatcherService _dispatcherService;

    private MiniPlayerWindow? _miniPlayerWindow;
    private bool _isMiniPlayerEnabled;
    private bool _isClosingMiniPlayerProgrammatically;
    private bool _isDisposed;

    /// <inheritdoc/>
    public event Action<AppWindowClosingEventArgs>? Closing;
    /// <inheritdoc/>
    public event Action<AppWindowChangedEventArgs>? VisibilityChanged;
    /// <inheritdoc/>
    public event Action? UIStateChanged;

    /// <inheritdoc/>
    public bool IsVisible => _appWindow.IsVisible;
    /// <inheritdoc/>
    public bool IsMiniPlayerActive => _miniPlayerWindow is not null;
    /// <inheritdoc/>
    public bool IsMinimized => _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
    /// <inheritdoc/>
    public bool IsExiting { get; set; }

    public WindowService(Window window, IWin32InteropService win32InteropService, IUISettingsService settingsService, IDispatcherService dispatcherService) {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _appWindow = window.AppWindow;
        _win32InteropService = win32InteropService ?? throw new ArgumentNullException(nameof(win32InteropService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));

        _appWindow.Closing += OnAppWindowClosing;
        _appWindow.Changed += OnAppWindowChanged;
        _settingsService.MinimizeToMiniPlayerSettingChanged += OnMinimizeToMiniPlayerSettingChanged;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync() {
        _isMiniPlayerEnabled = await _settingsService.GetMinimizeToMiniPlayerEnabledAsync();
        Debug.WriteLine($"[WindowService] Initial 'Minimize to Mini-Player' state: {_isMiniPlayerEnabled}");
    }

    /// <summary>
    /// Responds to live changes in the "Minimize to Mini-Player" application setting.
    /// </summary>
    private void OnMinimizeToMiniPlayerSettingChanged(bool isEnabled) {
        _isMiniPlayerEnabled = isEnabled;
        Debug.WriteLine($"[WindowService] 'Minimize to Mini-Player' setting updated to: {isEnabled}");
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args) {
        Closing?.Invoke(args);
    }

    /// <summary>
    /// Central handler for all AppWindow state changes, responsible for invoking high-level events.
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) {
        bool didPresenterChange = args.DidPresenterChange;

        if (args.DidVisibilityChange) {
            VisibilityChanged?.Invoke(args);
        }

        if (didPresenterChange && _appWindow.Presenter is OverlappedPresenter presenter) {
            switch (presenter.State) {
                case OverlappedPresenterState.Minimized:
                    if (_isMiniPlayerEnabled) {
                        _appWindow.IsShownInSwitchers = false;
                        ShowMiniPlayer();
                    }
                    break;

                case OverlappedPresenterState.Restored:
                    _appWindow.IsShownInSwitchers = true;
                    HideMiniPlayer();
                    break;
            }
        }

        // A change in either visibility or presenter state constitutes a UI state change
        // that external coordinators may need to react to.
        if (args.DidVisibilityChange || didPresenterChange) {
            UIStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Creates and displays the mini-player window.
    /// </summary>
    private void ShowMiniPlayer() {
        if (!_isMiniPlayerEnabled || _miniPlayerWindow is not null) {
            return;
        }

        _dispatcherService.TryEnqueue(() => {
            _miniPlayerWindow = new MiniPlayerWindow();
            _miniPlayerWindow.Closed += OnMiniPlayerClosed;
            _miniPlayerWindow.Activate();

            // Because the IsMiniPlayerActive state has changed, notify subscribers.
            UIStateChanged?.Invoke();
        });
    }

    /// <summary>
    /// Programmatically closes the mini-player window.
    /// </summary>
    private void HideMiniPlayer() {
        if (_miniPlayerWindow is null) return;

        _dispatcherService.TryEnqueue(() => {
            _isClosingMiniPlayerProgrammatically = true;
            _miniPlayerWindow?.Close();
            _isClosingMiniPlayerProgrammatically = false;
        });
    }

    /// <summary>
    /// Handles the Closed event for the mini-player, cleaning up resources and managing state transitions.
    /// </summary>
    private void OnMiniPlayerClosed(object? sender, WindowEventArgs args) {
        if (sender is MiniPlayerWindow window) {
            window.Closed -= OnMiniPlayerClosed;
        }

        // If the user manually closed the mini-player, restore the main application window
        // to provide a clear path back to the full UI.
        if (!_isClosingMiniPlayerProgrammatically) {
            Debug.WriteLine("[WindowService] Mini-player closed by user; restoring main window.");
            if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) {
                presenter.Restore();
            }
        }
        _miniPlayerWindow = null;

        // Because the IsMiniPlayerActive state has changed, notify subscribers.
        UIStateChanged?.Invoke();
    }

    /// <inheritdoc/>
    public void Hide() {
        _window.Hide(enableEfficiencyMode: false);
    }

    /// <inheritdoc/>
    public void ShowAndActivate() {
        HideMiniPlayer();
        _appWindow.IsShownInSwitchers = true;
        WindowActivator.ShowAndActivate(_window, _win32InteropService);
    }

    /// <inheritdoc/>
    public void MinimizeToMiniPlayer() {
        if (_appWindow.Presenter is OverlappedPresenter presenter) {
            presenter.Minimize();
        }
    }

    /// <inheritdoc/>
    public void Close() {
        _window.Close();
    }

    /// <inheritdoc/>
    public void SetEfficiencyMode(bool isEnabled) {
        EfficiencyModeUtilities.SetEfficiencyMode(isEnabled);
    }

    /// <summary>
    /// Cleans up resources and unsubscribes from events to prevent memory leaks.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        Debug.WriteLine("[WindowService] Disposing and cleaning up resources.");
        _appWindow.Closing -= OnAppWindowClosing;
        _appWindow.Changed -= OnAppWindowChanged;
        _settingsService.MinimizeToMiniPlayerSettingChanged -= OnMinimizeToMiniPlayerSettingChanged;

        _appWindow.IsShownInSwitchers = true;
        HideMiniPlayer();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}