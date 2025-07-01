using Microsoft.UI.Dispatching;
using Nagi.Helpers;
using Nagi.Popups;
using Nagi.Services.Abstractions;
using System;
using Windows.Graphics;
using WinRT.Interop;

namespace Nagi.Services.Implementations;

public class TrayPopupService : ITrayPopupService {
    // Vertical offset to position the popup above the cursor/taskbar.
    private const int VERTICAL_OFFSET = 24;
    // Debounce delay in milliseconds to prevent rapid toggling.
    private const uint DEACTIVATION_DEBOUNCE_MS = 200;

    private readonly IWin32InteropService _win32;
    private readonly DispatcherQueue _dispatcherQueue;
    private TrayPopup? _popupWindow;
    private uint _lastDeactivatedTime;
    private bool _isAnimating;

    public TrayPopupService(IWin32InteropService win32InteropService) {
        _win32 = win32InteropService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Cannot get DispatcherQueue for the current thread.");
    }

    public void ShowOrHidePopup() {
        // Prevent showing/hiding if an animation is in progress or if the window was just deactivated.
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

    private void ShowPopup() {
        _isAnimating = true;
        var appWindow = _popupWindow!.AppWindow;
        var windowHandle = WindowNative.GetWindowHandle(_popupWindow);

        var scale = _win32.GetDpiForWindow(windowHandle) / 96f;

        appWindow.Resize(new SizeInt32(
            (int)(370 * scale),
            (int)(360 * scale)));

        var cursorPosition = _win32.GetCursorPos();
        var workArea = _win32.GetWorkAreaForPoint(cursorPosition);
        var popupSize = appWindow.Size;

        // Center the popup horizontally on the cursor.
        int finalX = cursorPosition.X - (popupSize.Width / 2);
        // Clamp the horizontal position to stay within the work area.
        finalX = Math.Max((int)workArea.Left, finalX);
        finalX = Math.Min((int)workArea.Right - popupSize.Width, finalX);

        // Position the popup above the cursor.
        int finalY = cursorPosition.Y - popupSize.Height - VERTICAL_OFFSET;

        // If positioning above the cursor would push it off-screen, position it below instead.
        if (finalY < workArea.Top) {
            finalY = cursorPosition.Y + VERTICAL_OFFSET;
        }

        var finalPosition = new PointInt32(finalX, finalY);

        // Move the window to its final position first.
        appWindow.Move(finalPosition);

        // Use the "polite" activation method to prevent breaking the taskbar's open state.
        WindowActivator.ActivatePopupWindow(_popupWindow);

        // Call the animation method that just handles the visual effects.
        PopupAnimation.AnimateIn(_popupWindow, () => {
            _isAnimating = false;
        });
    }

    private void HidePopup() {
        if (_popupWindow != null && _popupWindow.AppWindow.IsVisible && !_isAnimating) {
            _isAnimating = true;
            PopupAnimation.Hide(_popupWindow, () => {
                _isAnimating = false;
            });
        }
    }

    private void CreateWindow() {
        _popupWindow = new TrayPopup();
        _popupWindow.Deactivated += OnPopupDeactivated;
        _popupWindow.Closed += (s, e) => {
            // Nullify the reference so it can be garbage collected and re-created on next show.
            _popupWindow = null;
        };
    }

    private void OnPopupDeactivated(object? sender, EventArgs e) {
        _lastDeactivatedTime = _win32.GetTickCount();
        HidePopup();
    }
}