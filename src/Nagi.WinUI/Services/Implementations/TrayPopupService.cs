using System.Threading.Tasks;
using Windows.Graphics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Popups;
using WinRT.Interop;

namespace Nagi.WinUI.Services.Implementations;

public class TrayPopupService : ITrayPopupService, IDisposable
{
    private const int VERTICAL_OFFSET = 24;
    private const int DEACTIVATION_DEBOUNCE_MS = 200;
    private const int POPUP_WIDTH_DIPS = 384;
    private const float BASE_DPI = 96.0f;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<TrayPopupService> _logger;
    private readonly MainWindow _mainWindow;
    private readonly Func<ElementTheme, TrayPopup> _createPopup;

    private readonly Win32InteropService _win32;
    private bool _isAnimating;
    private bool _isDisposed;
    private long _lastDeactivatedTime;
    private TrayPopup? _popupWindow;

    public TrayPopupService(MainWindow mainWindow, Func<ElementTheme, TrayPopup> createPopup,
        Win32InteropService win32InteropService,
        IDispatcherService dispatcherService, ILogger<TrayPopupService> logger)
    {
        _mainWindow = mainWindow;
        _createPopup = createPopup;
        _win32 = win32InteropService;
        _dispatcherService = dispatcherService;
        _logger = logger;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _logger.LogDebug("Disposing TrayPopupService.");

        // Cancel any ongoing animations first to prevent TaskCanceledException
        PopupAnimation.CancelAllAnimations();

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
        GC.SuppressFinalize(this);
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
            PopupAnimation.CancelAllAnimations();
            _popupWindow.AppWindow.Hide();
            _popupWindow.SetWindowOpacity(255);
            _isAnimating = false;
            return;
        }

        _isAnimating = true;
        await PopupAnimation.AnimateOut(_popupWindow);
        _isAnimating = false;
    }

    private async Task ShowPopupAsync()
    {
        _logger.LogDebug("Showing tray popup.");
        _isAnimating = true;
        var popup = _popupWindow!;
        popup.ViewModel.ShowPlayerViewCommand.Execute(null);

        var windowHandle = WindowNative.GetWindowHandle(popup);
        var scale = _win32.GetDpiForWindow(windowHandle) / BASE_DPI;

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

        var cursorPosition = _win32.GetCursorPos();
        var workArea = _win32.GetWorkAreaForPoint(cursorPosition);

        var finalX = cursorPosition.X - finalWidth / 2;
        finalX = (int)Math.Max(workArea.Left, Math.Min(workArea.Right - finalWidth, finalX));

        var finalY = cursorPosition.Y - finalHeight - VERTICAL_OFFSET;
        if (finalY < workArea.Top) finalY = cursorPosition.Y + VERTICAL_OFFSET;

        var finalRect = new RectInt32(finalX, finalY, finalWidth, finalHeight);
        _logger.LogDebug("Calculated popup position: {PopupRect}", finalRect);

        await PopupAnimation.AnimateIn(popup, finalRect);
        _isAnimating = false;
    }

    private void CreateWindow()
    {
        var mainContent = _mainWindow.Content as FrameworkElement;
        var currentTheme = mainContent?.ActualTheme ?? ElementTheme.Default;
        _logger.LogDebug("Creating new tray popup window with theme {Theme}.", currentTheme);
        _popupWindow = _createPopup(currentTheme);
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
