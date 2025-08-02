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
    public bool IsVisible => _appWindow.IsVisible;
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
        // Asynchronously load the initial setting for mini-player behavior.
        _isMiniPlayerEnabled = await _settingsService.GetMinimizeToMiniPlayerEnabledAsync();
        Debug.WriteLine($"[INFO] WindowService: Initial 'Minimize to Mini-Player' state: {_isMiniPlayerEnabled}");
    }

    /// <summary>
    /// Handles real-time changes to the "Minimize to Mini-Player" setting.
    /// </summary>
    /// <param name="isEnabled">The new value of the setting.</param>
    private void OnMinimizeToMiniPlayerSettingChanged(bool isEnabled) {
        _isMiniPlayerEnabled = isEnabled;
        Debug.WriteLine($"[INFO] WindowService: 'Minimize to Mini-Player' setting updated to: {isEnabled}");
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args) {
        Closing?.Invoke(args);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) {
        if (args.DidVisibilityChange) {
            VisibilityChanged?.Invoke(args);
        }

        if (args.DidPresenterChange && _appWindow.Presenter is OverlappedPresenter presenter) {
            Debug.WriteLine($"[INFO] WindowService: Main window presenter state changed to {presenter.State}.");
            switch (presenter.State) {
                case OverlappedPresenterState.Minimized:
                    if (_isMiniPlayerEnabled) {
                        EfficiencyModeUtilities.SetEfficiencyMode(false);
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
    }

    private void ShowMiniPlayer() {
        if (!_isMiniPlayerEnabled) return;

        if (_miniPlayerWindow is not null) {
            Debug.WriteLine("[WARN] WindowService: ShowMiniPlayer called, but an instance already exists.");
            return;
        }

        _dispatcherService.TryEnqueue(() => {
            _miniPlayerWindow = new MiniPlayerWindow();
            _miniPlayerWindow.Closed += OnMiniPlayerClosed;
            _miniPlayerWindow.Activate();
            EfficiencyModeUtilities.SetEfficiencyMode(true);
        });
    }

    private void HideMiniPlayer() {
        if (_miniPlayerWindow is null) return;

        _dispatcherService.TryEnqueue(() => {
            _isClosingMiniPlayerProgrammatically = true;
            _miniPlayerWindow?.Close();
            _isClosingMiniPlayerProgrammatically = false;
            Debug.WriteLine("[INFO] WindowService: Mini-player window closed programmatically.");
        });
    }

    private void OnMiniPlayerClosed(object? sender, WindowEventArgs args) {
        if (sender is MiniPlayerWindow window) {
            window.Closed -= OnMiniPlayerClosed;
        }

        // If the user closed the mini-player, restore the main window.
        if (!_isClosingMiniPlayerProgrammatically) {
            Debug.WriteLine("[INFO] WindowService: Mini-player closed by user; restoring main window.");
            if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) {
                presenter.Restore();
            }
        }
        _miniPlayerWindow = null;
    }

    /// <inheritdoc/>
    public void Hide() {
        _window.Hide(enableEfficiencyMode: true);
    }

    /// <inheritdoc/>
    public void ShowAndActivate() {
        HideMiniPlayer();
        _appWindow.IsShownInSwitchers = true;
        EfficiencyModeUtilities.SetEfficiencyMode(false);
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

    /// <summary>
    /// Cleans up resources and unsubscribes from events to prevent memory leaks.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        Debug.WriteLine("[INFO] WindowService: Disposing and cleaning up resources.");
        _appWindow.Closing -= OnAppWindowClosing;
        _appWindow.Changed -= OnAppWindowChanged;
        _settingsService.MinimizeToMiniPlayerSettingChanged -= OnMinimizeToMiniPlayerSettingChanged;

        _appWindow.IsShownInSwitchers = true;
        HideMiniPlayer();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}