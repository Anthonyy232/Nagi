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

public sealed class WindowService : IWindowService, IDisposable
{
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<WindowService> _logger;
    private readonly IUISettingsService _settingsService;
    private readonly IWin32InteropService _win32InteropService;
    private AppWindow? _appWindow;
    private bool _isClosingMiniPlayerProgrammatically;
    private bool _isDisposed;
    private volatile bool _isMiniPlayerEnabled;
    private bool? _lastEfficiencyMode;

    private readonly MainWindow _mainWindow;
    private readonly Func<MiniPlayerWindow> _createMiniPlayerWindow;
    private MiniPlayerWindow? _miniPlayerWindow;
    private Window? _window;

    public WindowService(MainWindow mainWindow, Func<MiniPlayerWindow> createMiniPlayerWindow,
        IWin32InteropService win32InteropService, IUISettingsService settingsService,
        IDispatcherService dispatcherService, ILogger<WindowService> logger)
    {
        _createMiniPlayerWindow = createMiniPlayerWindow;
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _win32InteropService = win32InteropService ?? throw new ArgumentNullException(nameof(win32InteropService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true; // Set FIRST to prevent race conditions in dispatched lambdas

        _logger.LogDebug("Disposing and cleaning up resources.");

        SetEfficiencyMode(false);

        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
            _appWindow.IsShownInSwitchers = true;
        }

        _settingsService.MinimizeToMiniPlayerSettingChanged -= OnMinimizeToMiniPlayerSettingChanged;

        // Synchronous cleanup of mini player state. Capture and null the reference
        // to prevent any in-flight operations from using a stale window.
        var window = _miniPlayerWindow;
        _miniPlayerWindow = null;

        // Attempt graceful close via dispatcher. If the dispatcher is unavailable
        // (shutdown), TryEnqueue will return false and the window will be cleaned
        // up when the process terminates.
        if (window is not null)
        {
            window.Closed -= OnMiniPlayerClosed;
            _dispatcherService.TryEnqueue(() =>
            {
                try
                {
                    window.Close();
                }
                catch
                {
                    // Ignore exceptions during shutdown
                }
            });
        }

        GC.SuppressFinalize(this);
    }

    public event Action<WindowClosingEventArgs>? Closing;

    public event Action? VisibilityChanged;

    public event Action? UIStateChanged;

    public bool IsVisible => _appWindow?.IsVisible ?? false;

    public bool IsMiniPlayerActive => _miniPlayerWindow is not null;

    public bool IsMinimized => _appWindow?.Presenter is OverlappedPresenter
    {
        State: OverlappedPresenterState.Minimized
    };

    public bool IsExiting { get; set; }

    public bool IsShowingOnboarding => _mainWindow.Content is Pages.OnboardingPage;

    public async Task InitializeAsync()
    {
        _window = _mainWindow;
        _appWindow = _window.AppWindow;

        _appWindow.Closing += OnAppWindowClosing;
        _appWindow.Changed += OnAppWindowChanged;
        _settingsService.MinimizeToMiniPlayerSettingChanged += OnMinimizeToMiniPlayerSettingChanged;

        _isMiniPlayerEnabled = await _settingsService.GetMinimizeToMiniPlayerEnabledAsync();
    }

    public void Hide()
    {
        if (_window is null) return;
        _window.Hide(false);
    }

    public void ShowAndActivate()
    {
        if (_window is null) return;

        // Force high priority immediately to prevent black screen lag during activation
        SetEfficiencyMode(false);

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var foregroundHandle = _win32InteropService.GetForegroundWindow();

        if (windowHandle == foregroundHandle && IsVisible && !IsMinimized && !IsMiniPlayerActive)
        {
            _logger.LogDebug("Window is already foreground and active, skipping redundant activation.");
            return;
        }

        HideMiniPlayer();
        if (_appWindow is not null) _appWindow.IsShownInSwitchers = true;
        WindowActivator.ShowAndActivate(_window, _win32InteropService);
    }

    public void Close()
    {
        _dispatcherService.TryEnqueue(() => _window?.Close());
    }

    public void SetEfficiencyMode(bool isEnabled)
    {
        if (_lastEfficiencyMode == isEnabled) return;

        _logger.LogDebug("Setting efficiency mode to: {IsEnabled}", isEnabled);
        EfficiencyModeUtilities.SetEfficiencyMode(isEnabled);
        _lastEfficiencyMode = isEnabled;
    }

    public void ShowMiniPlayer()
    {
        if (!_isMiniPlayerEnabled || _miniPlayerWindow is not null) return;

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;

            try
            {
                // Re-check inside the dispatched action to handle race conditions.
                if (!_isMiniPlayerEnabled || _miniPlayerWindow is not null) return;

                _miniPlayerWindow = _createMiniPlayerWindow();
                _miniPlayerWindow.Closed += OnMiniPlayerClosed;
                _miniPlayerWindow.Activate();

                UIStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to show Mini Player.");
                _miniPlayerWindow = null;
            }
        });
    }

    private void OnMinimizeToMiniPlayerSettingChanged(bool isEnabled)
    {
        _isMiniPlayerEnabled = isEnabled;
    }


    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var closing = new WindowClosingEventArgs();
        Closing?.Invoke(closing);
        if (closing.Cancel) args.Cancel = true;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        var didPresenterChange = args.DidPresenterChange;

        if (args.DidVisibilityChange) VisibilityChanged?.Invoke();

        if (didPresenterChange && _appWindow?.Presenter is OverlappedPresenter presenter)
            switch (presenter.State)
            {
                case OverlappedPresenterState.Minimized:
                    if (_isMiniPlayerEnabled)
                    {
                        _appWindow.IsShownInSwitchers = false;
                        ShowMiniPlayer();
                    }

                    break;

                case OverlappedPresenterState.Restored:
                    _appWindow.IsShownInSwitchers = true;
                    HideMiniPlayer();
                    break;
            }

        if (args.DidVisibilityChange || didPresenterChange) UIStateChanged?.Invoke();
    }

    private void HideMiniPlayer()
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;

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
                _isClosingMiniPlayerProgrammatically = false;
                _miniPlayerWindow = null;
            }
        });
    }

    private void OnMiniPlayerClosed(object? sender, WindowEventArgs args)
    {
        if (sender is MiniPlayerWindow window) window.Closed -= OnMiniPlayerClosed;
        _miniPlayerWindow = null;

        if (!_isClosingMiniPlayerProgrammatically) ShowAndActivate();

        UIStateChanged?.Invoke();
    }
}
