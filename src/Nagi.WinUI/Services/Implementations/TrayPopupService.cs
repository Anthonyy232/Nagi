using Microsoft.UI.Xaml;
using System;
using Windows.Graphics;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Popups;
using Nagi.WinUI.Services.Abstractions;
using WinRT.Interop;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
/// Manages the lifecycle and positioning of the tray popup window.
/// This service handles creating, showing, hiding, and animating the popup,
/// ensuring it is correctly positioned relative to the cursor and screen work area.
/// </summary>
public class TrayPopupService : ITrayPopupService {
    // Vertical distance between the cursor and the popup window.
    private const int VERTICAL_OFFSET = 24;
    // Time in milliseconds to ignore hide/show requests after deactivation to prevent flickering.
    private const uint DEACTIVATION_DEBOUNCE_MS = 200;
    // The width of the popup in device-independent pixels (DIPs).
    private const int POPUP_WIDTH_DIPS = 384;
    // The base DPI value used for scaling calculations.
    private const float BASE_DPI = 96.0f;

    private readonly IWin32InteropService _win32;
    private TrayPopup? _popupWindow;
    private uint _lastDeactivatedTime;
    private bool _isAnimating;

    public TrayPopupService(IWin32InteropService win32InteropService) {
        _win32 = win32InteropService;
    }

    public void ShowOrHidePopup() {
        // Prevent rapid toggling or actions during an animation.
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

        int finalWidth = (int)(POPUP_WIDTH_DIPS * scale);
        int finalHeight = (int)(_popupWindow!.GetContentDesiredHeight(POPUP_WIDTH_DIPS) * scale);

        var cursorPosition = _win32.GetCursorPos();
        var workArea = _win32.GetWorkAreaForPoint(cursorPosition);

        // Center the popup horizontally on the cursor.
        int finalX = cursorPosition.X - (finalWidth / 2);
        // Clamp the position to ensure it stays within the screen's work area.
        finalX = (int) Math.Max(workArea.Left, finalX);
        finalX = (int) Math.Min(workArea.Right - finalWidth, finalX);

        // Position the popup above the cursor by default.
        int finalY = cursorPosition.Y - finalHeight - VERTICAL_OFFSET;
        // If there's not enough space above, flip it to appear below the cursor.
        if (finalY < workArea.Top) {
            finalY = cursorPosition.Y + VERTICAL_OFFSET;
        }

        var finalRect = new RectInt32(finalX, finalY, finalWidth, finalHeight);

        await PopupAnimation.AnimateIn(_popupWindow, finalRect);

        _isAnimating = false;
    }

    private void CreateWindow() {
        var mainContent = App.RootWindow?.Content as FrameworkElement;
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