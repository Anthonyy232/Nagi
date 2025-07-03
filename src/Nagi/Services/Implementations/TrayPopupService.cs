using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Nagi.Helpers;
using Nagi.Popups;
using Nagi.Services.Abstractions;
using System;
using Windows.Graphics;
using WinRT.Interop;

namespace Nagi.Services.Implementations;

/// <summary>
/// Manages the lifecycle, positioning, and animation of the tray icon's popup window.
/// </summary>
public class TrayPopupService : ITrayPopupService {
    //
    // The vertical distance between the cursor and the popup window.
    //
    private const int VERTICAL_OFFSET = 24;
    //
    // Prevents the popup from reappearing immediately after being closed.
    //
    private const uint DEACTIVATION_DEBOUNCE_MS = 200;
    private const int POPUP_WIDTH_DIPS = 384;

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

    /// <summary>
    /// Shows the popup if it is hidden, or hides it if it is visible.
    /// Applies a debounce to prevent rapid toggling.
    /// </summary>
    public void ShowOrHidePopup() {
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

    /// <summary>
    /// Hides the popup window if it is currently visible.
    /// </summary>
    public void HidePopup() {
        if (_popupWindow != null && _popupWindow.AppWindow.IsVisible && !_isAnimating) {
            _isAnimating = true;
            PopupAnimation.Hide(_popupWindow, () => {
                _isAnimating = false;
            });
        }
    }

    private void ShowPopup() {
        _isAnimating = true;
        var appWindow = _popupWindow!.AppWindow;
        var windowHandle = WindowNative.GetWindowHandle(_popupWindow);

        var scale = _win32.GetDpiForWindow(windowHandle) / 96f;

        int desiredWidthPhysical = (int)(POPUP_WIDTH_DIPS * scale);
        int desiredHeightPhysical = (int)(_popupWindow.GetContentDesiredHeight(POPUP_WIDTH_DIPS) * scale);

        appWindow.Resize(new SizeInt32(desiredWidthPhysical, desiredHeightPhysical));

        //
        // Calculate the optimal position for the popup near the cursor, ensuring it stays within the work area.
        //
        var cursorPosition = _win32.GetCursorPos();
        var workArea = _win32.GetWorkAreaForPoint(cursorPosition);
        var popupSize = appWindow.Size;

        int finalX = cursorPosition.X - (popupSize.Width / 2);
        finalX = Math.Max((int)workArea.Left, finalX);
        finalX = Math.Min((int)workArea.Right - popupSize.Width, finalX);

        //
        // Position the popup above the cursor, but flip it below if there is not enough space.
        //
        int finalY = cursorPosition.Y - popupSize.Height - VERTICAL_OFFSET;
        if (finalY < workArea.Top) {
            finalY = cursorPosition.Y + VERTICAL_OFFSET;
        }

        appWindow.Move(new PointInt32(finalX, finalY));
        WindowActivator.ActivatePopupWindow(_popupWindow);

        PopupAnimation.AnimateIn(_popupWindow, () => {
            _isAnimating = false;
        });
    }

    private void CreateWindow() {
        //
        // Determine the current theme from the main window's content to prevent a theme flash on popup creation.
        // This assumes the App class exposes a reference to the main window.
        //
        var mainContent = (App.Current as App)?._window?.Content as FrameworkElement;
        var currentTheme = mainContent?.ActualTheme ?? ElementTheme.Default;

        _popupWindow = new TrayPopup(currentTheme);
        _popupWindow.Deactivated += OnPopupDeactivated;
        _popupWindow.Closed += (s, e) => {
            _popupWindow = null;
        };
    }

    private void OnPopupDeactivated(object? sender, EventArgs e) {
        _lastDeactivatedTime = _win32.GetTickCount();
        HidePopup();
    }
}