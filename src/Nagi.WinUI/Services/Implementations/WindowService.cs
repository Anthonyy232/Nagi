using System;
using System.Threading.Tasks;
using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Manages the state and interactions of the application's main window and the secondary mini-player window.
/// </summary>
public sealed class WindowService : IWindowService, IDisposable
{
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<WindowService> _logger;
    private readonly IUISettingsService _settingsService;
    private readonly IWin32InteropService _win32InteropService;
    private AppWindow? _appWindow;
    private bool _isClosingMiniPlayerProgrammatically;
    private bool _isDisposed;
    private bool _isMiniPlayerEnabled;

    private MiniPlayerWindow? _miniPlayerWindow;
    private Window? _window;

    public WindowService(IWin32InteropService win32InteropService, IUISettingsService settingsService,
        IDispatcherService dispatcherService, ILogger<WindowService> logger)
    {
        _win32InteropService = win32InteropService ?? throw new ArgumentNullException(nameof(win32InteropService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Cleans up resources and unsubscribes from events to prevent memory leaks.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        _logger.LogDebug("Disposing and cleaning up resources.");

        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
            _appWindow.IsShownInSwitchers = true;
        }

        _settingsService.MinimizeToMiniPlayerSettingChanged -= OnMinimizeToMiniPlayerSettingChanged;

        HideMiniPlayer();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public event Action<AppWindowClosingEventArgs>? Closing;

    /// <inheritdoc />
    public event Action<AppWindowChangedEventArgs>? VisibilityChanged;

    /// <inheritdoc />
    public event Action? UIStateChanged;

    /// <inheritdoc />
    public bool IsVisible => _appWindow?.IsVisible ?? false;

    /// <inheritdoc />
    public bool IsMiniPlayerActive => _miniPlayerWindow is not null;

    /// <inheritdoc />
    public bool IsMinimized => _appWindow?.Presenter is OverlappedPresenter
    {
        State: OverlappedPresenterState.Minimized
    };

    /// <inheritdoc />
    public bool IsExiting { get; set; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _window = App.RootWindow ??
                  throw new InvalidOperationException("Root window is not available for WindowService initialization.");
        _appWindow = _window.AppWindow;

        _appWindow.Closing += OnAppWindowClosing;
        _appWindow.Changed += OnAppWindowChanged;
        _settingsService.MinimizeToMiniPlayerSettingChanged += OnMinimizeToMiniPlayerSettingChanged;

        _isMiniPlayerEnabled = await _settingsService.GetMinimizeToMiniPlayerEnabledAsync();
    }

    /// <inheritdoc />
    public void Hide()
    {
        if (_window is null) return;
        _window.Hide(false);
    }

    /// <inheritdoc />
    public void ShowAndActivate()
    {
        if (_window is null) return;
        HideMiniPlayer();
        if (_appWindow is not null) _appWindow.IsShownInSwitchers = true;
        WindowActivator.ShowAndActivate(_window, _win32InteropService);
    }

    /// <inheritdoc />
    public void MinimizeToMiniPlayer()
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }

    /// <inheritdoc />
    public void Close()
    {
        _window?.Close();
    }

    /// <inheritdoc />
    public void SetEfficiencyMode(bool isEnabled)
    {
        EfficiencyModeUtilities.SetEfficiencyMode(isEnabled);
    }

    /// <summary>
    ///     Creates and displays the mini-player window.
    /// </summary>
    public void ShowMiniPlayer()
    {
        if (!_isMiniPlayerEnabled || _miniPlayerWindow is not null) return;

        _dispatcherService.TryEnqueue(() =>
        {
            try
            {
                // Re-check inside the dispatched action to handle race conditions.
                if (!_isMiniPlayerEnabled || _miniPlayerWindow is not null) return;

                _miniPlayerWindow = new MiniPlayerWindow();
                _miniPlayerWindow.Closed += OnMiniPlayerClosed;
                _miniPlayerWindow.Activate();

                // Because the IsMiniPlayerActive state has changed, notify subscribers.
                UIStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to show Mini Player.");
                // Ensure we clean up if the window failed to initialize.
                _miniPlayerWindow = null;
            }
        });
    }

    /// <summary>
    ///     Responds to live changes in the "Minimize to Mini-Player" application setting.
    /// </summary>
    private void OnMinimizeToMiniPlayerSettingChanged(bool isEnabled)
    {
        _isMiniPlayerEnabled = isEnabled;
    }


    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        Closing?.Invoke(args);
    }

    /// <summary>
    ///     Central handler for all AppWindow state changes, responsible for invoking high-level events.
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        var didPresenterChange = args.DidPresenterChange;

        if (args.DidVisibilityChange) VisibilityChanged?.Invoke(args);

        if (didPresenterChange && _appWindow?.Presenter is OverlappedPresenter presenter)
            switch (presenter.State)
            {
                case OverlappedPresenterState.Minimized:
                    if (_isMiniPlayerEnabled)
                    {
                        if (_appWindow is not null) _appWindow.IsShownInSwitchers = false;
                        ShowMiniPlayer();
                    }

                    break;

                case OverlappedPresenterState.Restored:
                    if (_appWindow is not null) _appWindow.IsShownInSwitchers = true;
                    HideMiniPlayer();
                    break;
            }

        // A change in either visibility or presenter state constitutes a UI state change
        // that external coordinators may need to react to.
        if (args.DidVisibilityChange || didPresenterChange) UIStateChanged?.Invoke();
    }

    /// <summary>
    ///     Programmatically closes the mini-player window.
    /// </summary>
    private void HideMiniPlayer()
    {
        if (_miniPlayerWindow is null) return;

        _dispatcherService.TryEnqueue(() =>
        {
            try
            {
                if (_miniPlayerWindow is null) return;

                _isClosingMiniPlayerProgrammatically = true;
                _miniPlayerWindow.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while hiding Mini Player.");
            }
            finally
            {
                // Ensure state is reset even if Close() fails.
                _isClosingMiniPlayerProgrammatically = false;
                _miniPlayerWindow = null;
            }
        });
    }

    /// <summary>
    ///     Handles the Closed event for the mini-player, cleaning up resources and managing state transitions.
    /// </summary>
    private void OnMiniPlayerClosed(object? sender, WindowEventArgs args)
    {
        if (sender is MiniPlayerWindow window) window.Closed -= OnMiniPlayerClosed;

        // If the user manually closed the mini-player, show the main application window
        if (!_isClosingMiniPlayerProgrammatically)
        {
            _miniPlayerWindow = null;
            ShowAndActivate();
        }

        _miniPlayerWindow = null;
        UIStateChanged?.Invoke();
    }
}