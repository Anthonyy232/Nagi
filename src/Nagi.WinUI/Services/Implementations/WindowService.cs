using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nagi.Core.Platform;
using Nagi.Presentation.Services.Abstractions;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;
using WinRT.Interop;

namespace Nagi.WinUI.Services.Implementations;

public sealed class WindowService : IWindowService, IDisposable
{
    private readonly MainWindow _mainWindow;
    private readonly AppWindow _appWindow;
    private readonly Func<MiniPlayerWindow> _createMiniPlayerWindow;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<WindowService> _logger;
    private MiniPlayerWindow? _miniPlayerWindow;
    private bool _isDisposed;
    private bool? _lastEfficiencyMode;

    public WindowService(MainWindow mainWindow, Func<MiniPlayerWindow> createMiniPlayerWindow,
        IDispatcherService dispatcherService, ILogger<WindowService> logger)
    {
        _mainWindow = mainWindow;
        _appWindow = mainWindow.AppWindow;
        _createMiniPlayerWindow = createMiniPlayerWindow;
        _dispatcherService = dispatcherService;
        _logger = logger;

        _appWindow.Closing += OnAppWindowClosing;
        _mainWindow.Closed += OnWindowClosed;
        _appWindow.Changed += OnAppWindowChanged;
    }

    public event Action<WindowClosingEventArgs>? Closing;

    public event Action? Minimized;

    public event Action? UIStateChanged;

    public bool IsVisible => _appWindow.IsVisible;

    public bool IsMiniPlayerActive => _miniPlayerWindow is not null;

    public bool IsMinimized => _appWindow.Presenter is OverlappedPresenter
    {
        State: OverlappedPresenterState.Minimized
    };

    public bool IsExiting { get; private set; }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _logger.LogDebug("Disposing and cleaning up resources.");

        SetEfficiencyMode(false);

        _appWindow.Closing -= OnAppWindowClosing;
        _mainWindow.Closed -= OnWindowClosed;
        _appWindow.Changed -= OnAppWindowChanged;
        _appWindow.IsShownInSwitchers = true;

        var miniPlayer = _miniPlayerWindow;
        _miniPlayerWindow = null;
        if (miniPlayer is not null)
        {
            miniPlayer.RestoreRequested -= ShowAndActivate;
            miniPlayer.Closed -= OnMiniPlayerClosed;
            // During shutdown the dispatcher may refuse this or the close may fail; the window then goes with the process.
            _dispatcherService.TryEnqueue(() =>
            {
                try
                {
                    miniPlayer.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not close the mini player during shutdown.");
                }
            });
        }
    }

    public void Hide() => _mainWindow.Hide(false);

    public void ShowAndActivate()
    {
        // Force high priority immediately to prevent black screen lag during activation
        SetEfficiencyMode(false);

        var windowHandle = WindowNative.GetWindowHandle(_mainWindow);
        if (windowHandle == NativeMethods.GetForegroundWindow() && IsVisible && !IsMinimized && !IsMiniPlayerActive)
        {
            _logger.LogDebug("Window is already foreground and active, skipping redundant activation.");
            return;
        }

        HideMiniPlayer();
        _appWindow.IsShownInSwitchers = true;
        BringToForeground(windowHandle);
    }

    public void ShowMinimized()
    {
        _appWindow.Show(false);
        ((FrameworkElement)_mainWindow.Content).UpdateLayout();
        ((OverlappedPresenter)_appWindow.Presenter).Minimize(false);
    }

    public void ShowMiniPlayer()
    {
        if (_miniPlayerWindow is not null) return;
        _appWindow.IsShownInSwitchers = false;

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed || _miniPlayerWindow is not null) return;

            try
            {
                _miniPlayerWindow = _createMiniPlayerWindow();
                _miniPlayerWindow.RestoreRequested += ShowAndActivate;
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

    public void Exit()
    {
        IsExiting = true;
        _dispatcherService.TryEnqueue(_mainWindow.Close);
    }

    public void SetEfficiencyMode(bool isEnabled)
    {
        if (_lastEfficiencyMode == isEnabled) return;

        _logger.LogDebug("Setting efficiency mode to: {IsEnabled}", isEnabled);
        EfficiencyModeUtilities.SetEfficiencyMode(isEnabled);
        _lastEfficiencyMode = isEnabled;
    }

    // Windows only lets the foreground process take the foreground, so borrow its input thread while activating.
    private void BringToForeground(nint windowHandle)
    {
        var foregroundThreadId = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), 0);
        var currentThreadId = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThreadId != 0 && foregroundThreadId != currentThreadId &&
                       NativeMethods.AttachThreadInput(foregroundThreadId, currentThreadId, true);
        try
        {
            NativeMethods.BringWindowToTop(windowHandle);
            // SW_RESTORE also un-maximizes, so only use it to bring the window back from minimized.
            if (IsMinimized) NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_RESTORE);
            _appWindow.Show();
            NativeMethods.SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(foregroundThreadId, currentThreadId, false);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var closing = new WindowClosingEventArgs();
        Closing?.Invoke(closing);
        if (closing.Cancel) args.Cancel = true;
        else IsExiting = true;
    }

    // Also covers closes that skip Closing, so that nothing replaces the content of a closing window.
    private void OnWindowClosed(object sender, WindowEventArgs args) => IsExiting = true;

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange && _appWindow.Presenter is OverlappedPresenter presenter)
            switch (presenter.State)
            {
                case OverlappedPresenterState.Minimized:
                    Minimized?.Invoke();
                    break;

                case OverlappedPresenterState.Restored:
                    _appWindow.IsShownInSwitchers = true;
                    HideMiniPlayer();
                    break;
            }

        if (args.DidVisibilityChange || args.DidPresenterChange) UIStateChanged?.Invoke();
    }

    private void HideMiniPlayer()
    {
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed || _miniPlayerWindow is null) return;

            try
            {
                _miniPlayerWindow.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while hiding Mini Player.");
                _miniPlayerWindow = null;
            }
        });
    }

    private void OnMiniPlayerClosed(object? sender, WindowEventArgs args)
    {
        var miniPlayer = (MiniPlayerWindow)sender!;
        miniPlayer.RestoreRequested -= ShowAndActivate;
        miniPlayer.Closed -= OnMiniPlayerClosed;
        _miniPlayerWindow = null;

        // Closing the mini player any other way than restoring, such as with Alt+F4, must not leave the main
        // window hidden or minimized out of the taskbar.
        if (!IsExiting && (!IsVisible || IsMinimized)) ShowAndActivate();

        UIStateChanged?.Invoke();
    }
}
