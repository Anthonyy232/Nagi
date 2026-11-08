using System.Threading.Tasks;
using Windows.Graphics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nagi.Core.Platform;
using Nagi.Presentation.Shell;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Popups;
using WinRT.Interop;

namespace Nagi.WinUI.Services.Implementations;

public class TrayPopupService : ITrayPopupService, IDisposable
{
    private const int VERTICAL_OFFSET = 24;
    private const int DEACTIVATION_DEBOUNCE_MS = 200;
    private const int POPUP_WIDTH_DIPS = 384;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<TrayPopupService> _logger;
    private readonly Func<TrayPopup> _createPopup;

    private readonly TrayPopupAnimation _animation;
    private bool _isAnimating;
    private bool _isDisposed;
    private long _lastDeactivatedTime;
    private TrayPopup? _popupWindow;

    public TrayPopupService(Func<TrayPopup> createPopup,
        IDispatcherService dispatcherService, ILogger<TrayPopupService> logger)
    {
        _createPopup = createPopup;
        _animation = new TrayPopupAnimation(logger);
        _dispatcherService = dispatcherService;
        _logger = logger;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _logger.LogDebug("Disposing TrayPopupService.");

        // Cancel any ongoing animations first to prevent TaskCanceledException
        _animation.Cancel();

        // Explicitly unsubscribe before closing to prevent callbacks during disposal
        if (_popupWindow is not null)
        {
            _popupWindow.Deactivated -= OnPopupDeactivated;
            _popupWindow.Closed -= OnPopupWindowClosed;
        }

        var popup = _popupWindow;
        _popupWindow = null;

        if (popup != null)
        {
            _dispatcherService.TryEnqueue(() => popup.Close());
        }

        _isDisposed = true;
    }

    public void TogglePopup()
    {
        if (_isDisposed)
        {
            _logger.LogWarning("TogglePopup called on a disposed service.");
            return;
        }

        if (_isAnimating || Environment.TickCount64 - _lastDeactivatedTime < DEACTIVATION_DEBOUNCE_MS)
        {
            _logger.LogDebug("Popup toggle debounced (Animating: {IsAnimating}, Time since deactivation: {Time}ms)",
                _isAnimating, Environment.TickCount64 - _lastDeactivatedTime);
            return;
        }

        if (_popupWindow == null) CreateWindow();

        if (_popupWindow!.AppWindow.IsVisible)
            _ = HidePopupAsync();
        else
            _ = ShowPopupAsync();
    }

    public void HidePopup() => _ = HidePopupAsync();

    private async Task HidePopupAsync()
    {
        if (_popupWindow == null || !_popupWindow.AppWindow.IsVisible) return;

        _logger.LogDebug("Hiding tray popup.");
        if (_isAnimating)
        {
            _animation.Cancel();
            _popupWindow.AppWindow.Hide();
            _popupWindow.SetWindowOpacity(255);
            _isAnimating = false;
            return;
        }

        _isAnimating = true;
        await _animation.AnimateOutAsync(_popupWindow);
        _isAnimating = false;
    }

    private async Task ShowPopupAsync()
    {
        _logger.LogDebug("Showing tray popup.");
        _isAnimating = true;
        var popup = _popupWindow!;
        popup.ShowPlayerView();

        var windowHandle = WindowNative.GetWindowHandle(popup);
        var scale = NativeMethods.GetDpiScale(windowHandle);

        var contentHeight = await popup.ShowAndMeasureAsync(POPUP_WIDTH_DIPS);
        if (_popupWindow != popup)
        {
            // The popup closed while measuring; leaving the flag set would block every later toggle.
            _isAnimating = false;
            return;
        }

        popup.AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Ceiling(POPUP_WIDTH_DIPS * scale),
            (int)Math.Ceiling(contentHeight * scale)));
        var finalWidth = popup.AppWindow.Size.Width;
        var finalHeight = popup.AppWindow.Size.Height;

        var cursorPosition = NativeMethods.GetCursorPosition();
        var workArea = DisplayArea.GetFromPoint(cursorPosition, DisplayAreaFallback.Nearest).WorkArea;

        var finalX = cursorPosition.X - finalWidth / 2;
        finalX = Math.Max(workArea.X, Math.Min(workArea.X + workArea.Width - finalWidth, finalX));

        var finalY = cursorPosition.Y - finalHeight - VERTICAL_OFFSET;
        if (finalY < workArea.Y) finalY = cursorPosition.Y + VERTICAL_OFFSET;

        var finalRect = new RectInt32(finalX, finalY, finalWidth, finalHeight);
        _logger.LogDebug("Calculated popup position: {PopupRect}", finalRect);

        await _animation.AnimateInAsync(popup, finalRect);
        _isAnimating = false;
    }

    private void CreateWindow()
    {
        _logger.LogDebug("Creating the tray popup window.");
        _popupWindow = _createPopup();
        _popupWindow.Deactivated += OnPopupDeactivated;
        _popupWindow.Closed += OnPopupWindowClosed;
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        _lastDeactivatedTime = Environment.TickCount64;
        _logger.LogDebug("Popup deactivated. Hiding.");
        _ = HidePopupAsync();
    }

    private void OnPopupWindowClosed(object? sender, WindowEventArgs args)
    {
        if (sender is TrayPopup popup)
        {
            _logger.LogDebug("Tray popup window closed. Cleaning up references.");
            popup.Deactivated -= OnPopupDeactivated;
            popup.Closed -= OnPopupWindowClosed;
            _popupWindow = null;
        }
    }
}
