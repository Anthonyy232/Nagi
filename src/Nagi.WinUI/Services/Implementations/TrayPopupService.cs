using System;
using Windows.Graphics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Popups;
using Nagi.WinUI.Services.Abstractions;
using WinRT.Interop;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Manages the lifecycle and positioning of the tray popup window.
/// </summary>
public class TrayPopupService : ITrayPopupService, IDisposable {
    private const int VERTICAL_OFFSET = 24;
    private const uint DEACTIVATION_DEBOUNCE_MS = 200;
    private const int POPUP_WIDTH_DIPS = 384;
    private const float BASE_DPI = 96.0f;

    private readonly IWin32InteropService _win32;
    private readonly ILogger<TrayPopupService> _logger;
    private bool _isAnimating;
    private bool _isDisposed;
    private uint _lastDeactivatedTime;
    private TrayPopup? _popupWindow;

    public TrayPopupService(IWin32InteropService win32InteropService, ILogger<TrayPopupService> logger) {
        _win32 = win32InteropService;
        _logger = logger;
    }

    public void Dispose() {
        if (_isDisposed) return;
        _logger.LogInformation("Disposing TrayPopupService.");
        _popupWindow?.Close();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    public void ShowOrHidePopup() {
        if (_isDisposed) {
            _logger.LogWarning("ShowOrHidePopup called on a disposed service.");
            return;
        }

        if (_isAnimating || _win32.GetTickCount() - _lastDeactivatedTime < DEACTIVATION_DEBOUNCE_MS) {
            _logger.LogDebug("Popup toggle debounced (Animating: {IsAnimating}, Time since deactivation: {Time}ms)",
                _isAnimating, _win32.GetTickCount() - _lastDeactivatedTime);
            return;
        }

        if (_popupWindow == null) CreateWindow();

        if (_popupWindow!.AppWindow.IsVisible)
            HidePopup();
        else
            ShowPopup();
    }

    public async void HidePopup() {
        if (_popupWindow != null && _popupWindow.AppWindow.IsVisible && !_isAnimating) {
            _logger.LogInformation("Hiding tray popup.");
            _isAnimating = true;
            await PopupAnimation.AnimateOut(_popupWindow);
            _isAnimating = false;
        }
    }

    private async void ShowPopup() {
        _logger.LogInformation("Showing tray popup.");
        _isAnimating = true;
        _popupWindow!.ViewModel.ShowPlayerViewCommand.Execute(null);

        var windowHandle = WindowNative.GetWindowHandle(_popupWindow!);
        var scale = _win32.GetDpiForWindow(windowHandle) / BASE_DPI;

        var finalWidth = (int)(POPUP_WIDTH_DIPS * scale);
        var finalHeight = (int)(_popupWindow!.GetContentDesiredHeight(POPUP_WIDTH_DIPS) * scale);

        var cursorPosition = _win32.GetCursorPos();
        var workArea = _win32.GetWorkAreaForPoint(cursorPosition);

        var finalX = cursorPosition.X - finalWidth / 2;
        finalX = (int)Math.Max(workArea.Left, Math.Min(workArea.Right - finalWidth, finalX));

        var finalY = cursorPosition.Y - finalHeight - VERTICAL_OFFSET;
        if (finalY < workArea.Top) finalY = cursorPosition.Y + VERTICAL_OFFSET;

        var finalRect = new RectInt32(finalX, finalY, finalWidth, finalHeight);
        _logger.LogDebug("Calculated popup position: {PopupRect}", finalRect);

        await PopupAnimation.AnimateIn(_popupWindow, finalRect);
        _isAnimating = false;
    }

    private void CreateWindow() {
        var mainContent = App.RootWindow?.Content as FrameworkElement;
        var currentTheme = mainContent?.ActualTheme ?? ElementTheme.Default;
        _logger.LogInformation("Creating new tray popup window with theme {Theme}.", currentTheme);
        _popupWindow = new TrayPopup(currentTheme);
        _popupWindow.Deactivated += OnPopupDeactivated;
        _popupWindow.Closed += OnPopupWindowClosed;
    }

    private void OnPopupDeactivated(object? sender, EventArgs e) {
        _lastDeactivatedTime = _win32.GetTickCount();
        _logger.LogDebug("Popup deactivated. Hiding.");
        HidePopup();
    }



    private void OnPopupWindowClosed(object? sender, WindowEventArgs args) {
        if (sender is TrayPopup popup) {
            _logger.LogInformation("Tray popup window closed. Cleaning up references.");
            popup.Deactivated -= OnPopupDeactivated;
            popup.Closed -= OnPopupWindowClosed;
            _popupWindow = null;
        }
    }
}