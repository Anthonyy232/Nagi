using Microsoft.UI.Xaml;
using Nagi.Helpers;
using Nagi.Popups;
using Nagi.Services.Abstractions;
using System;
using Windows.Graphics;
using WinRT.Interop;

namespace Nagi.Services.Implementations;

/// <summary>
/// Manages the lifecycle and positioning of the tray popup window.
/// </summary>
public class TrayPopupService : ITrayPopupService {
    // The vertical distance between the cursor and the popup window.
    private const int VERTICAL_OFFSET = 24;
    // A debounce delay to prevent the popup from immediately reappearing after being closed.
    private const uint DEACTIVATION_DEBOUNCE_MS = 200;
    // The fixed width of the popup in device-independent pixels (DIPs).
    private const int POPUP_WIDTH_DIPS = 384;
    // The standard DPI value that DIPs are based on.
    private const float BASE_DPI = 96.0f;

    private readonly IWin32InteropService _win32;
    private TrayPopup? _popupWindow;
    private uint _lastDeactivatedTime;
    // A flag to prevent starting a new animation while one is already in progress.
    private bool _isAnimating;

    public TrayPopupService(IWin32InteropService win32InteropService) {
        _win32 = win32InteropService;
    }

    public void ShowOrHidePopup() {
        // Prevent showing/hiding if an animation is running or if the popup was just deactivated.
        if (_isAnimating || (_win32.GetTickCount() - _lastDeactivatedTime < DEACTIVATION_DEBOUNCE_MS)) {
            return;
        }

        if (_popupWindow == null) {
            CreateWindow();
        }

        if (_popupWindow!.AppWindow.IsVisible) {
            HidePopup();
        }
        else {
            ShowPopup();
        }
    }

    public async void HidePopup() {
        if (_popupWindow != null && _popupWindow.AppWindow.IsVisible && !_isAnimating) {
            _isAnimating = true;
            await PopupAnimation.AnimateOut(_popupWindow);
            _isAnimating = false;
        }
    }

    private async void ShowPopup() {
        _isAnimating = true;

        var windowHandle = WindowNative.GetWindowHandle(_popupWindow!);
        var scale = _win32.GetDpiForWindow(windowHandle) / BASE_DPI;

        // Calculate the final size of the window based on its content and the screen's DPI scaling.
        int finalWidth = (int)(POPUP_WIDTH_DIPS * scale);
        int finalHeight = (int)(_popupWindow!.GetContentDesiredHeight(POPUP_WIDTH_DIPS) * scale);

        // Determine the optimal position for the popup.
        var cursorPosition = _win32.GetCursorPos();
        var workArea = _win32.GetWorkAreaForPoint(cursorPosition);

        // Center the popup horizontally on the cursor, but clamp it within the work area.
        int finalX = cursorPosition.X - (finalWidth / 2);
        finalX = Math.Max((int)workArea.Left, finalX);
        finalX = Math.Min((int)workArea.Right - finalWidth, finalX);

        // Position the popup above the cursor, but flip it below if there isn't enough space.
        int finalY = cursorPosition.Y - finalHeight - VERTICAL_OFFSET;
        if (finalY < workArea.Top) {
            finalY = cursorPosition.Y + VERTICAL_OFFSET;
        }

        var finalRect = new RectInt32(finalX, finalY, finalWidth, finalHeight);

        await PopupAnimation.AnimateIn(_popupWindow, finalRect);

        _isAnimating = false;
    }

    private void CreateWindow() {
        var mainContent = (App.Current as App)?._window?.Content as FrameworkElement;
        var currentTheme = mainContent?.ActualTheme ?? ElementTheme.Default;
        _popupWindow = new TrayPopup(currentTheme);
        _popupWindow.Deactivated += OnPopupDeactivated;
        _popupWindow.Closed += (s, e) => { _popupWindow = null; };
    }

    private void OnPopupDeactivated(object? sender, EventArgs e) {
        _lastDeactivatedTime = _win32.GetTickCount();
        HidePopup();
    }
}