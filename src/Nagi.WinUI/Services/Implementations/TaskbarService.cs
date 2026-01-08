using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;
using Windows.Foundation;
using Windows.UI;
using static Nagi.WinUI.Helpers.TaskbarNativeMethods;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Provides Windows taskbar thumbnail toolbar integration for media playback controls.
///     This service creates and manages the previous, play/pause, and next buttons that appear
///     in the taskbar thumbnail preview when hovering over the application icon.
/// </summary>
/// <remarks>
///     This service uses native Win32 interop via ITaskbarList4 to register thumbnail buttons
///     and handle button click events through window message processing. Icons are rendered
///     using Win2D for reliable, GPU-accelerated rendering without visual tree dependencies.
/// </remarks>
public sealed class TaskbarService : ITaskbarService
{
    // Button IDs for the taskbar thumbnail toolbar.
    private const int PreviousButtonId = 1;
    private const int PlayPauseButtonId = 2;
    private const int NextButtonId = 3;

    // Debounce delay for icon refresh to prevent rapid updates during theme changes.
    private const int IconRefreshDebounceMs = 100;

    // Default icon size when DPI cannot be determined.
    private const int DefaultIconSize = 16;

    // Standard DPI base value.
    private const double BaseDpi = 96.0;

    // Font size multiplier for the icon within the render target (provides slight padding).
    private const float FontSizeMultiplier = 0.85f;

    // Glyph codes for media control icons (Segoe MDL2 Assets).
    private const string PreviousGlyph = "\xE892";
    private const string PlayGlyph = "\xE768";
    private const string PauseGlyph = "\xE769";
    private const string NextGlyph = "\xE893";

    // Icon font family name.
    private const string IconFontFamily = "Segoe MDL2 Assets";

    private readonly ILogger<TaskbarService> _logger;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IDispatcherService _dispatcherService;
    private readonly IWin32InteropService _win32InteropService;

    private readonly object _iconLock = new();

    private ITaskbarList4? _taskbarList;
    private nint _windowHandle;
    private bool _isInitialized;
    private bool _isDisposed;
    private uint _wmTaskbarButtonCreated;

    private THUMBBUTTON[]? _buttons;
    private nint _prevIcon;
    private nint _nextIcon;
    private nint _playIcon;
    private nint _pauseIcon;

    private CancellationTokenSource? _iconRefreshCts;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TaskbarService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance for diagnostic output.</param>
    /// <param name="playbackService">The music playback service to control.</param>
    /// <param name="dispatcherService">The dispatcher service for UI thread marshaling.</param>
    /// <param name="win32InteropService">The Win32 interop service for DPI and other functions.</param>
    public TaskbarService(
        ILogger<TaskbarService> logger,
        IMusicPlaybackService playbackService,
        IDispatcherService dispatcherService,
        IWin32InteropService win32InteropService)
    {
        _logger = logger;
        _playbackService = playbackService;
        _dispatcherService = dispatcherService;
        _win32InteropService = win32InteropService;
    }

    /// <inheritdoc />
    public void Initialize(nint windowHandle)
    {
        if (_isInitialized)
        {
            _logger.LogWarning("TaskbarService is already initialized");
            return;
        }

        _windowHandle = windowHandle;

        try
        {
            var taskbarListType = Type.GetTypeFromCLSID(TaskbarListGuid);
            if (taskbarListType == null)
            {
                _logger.LogError("Failed to get TaskbarList4 type from CLSID. Taskbar integration disabled");
                return;
            }

            var instance = Activator.CreateInstance(taskbarListType);
            if (instance is not ITaskbarList4 taskbarList)
            {
                _logger.LogError("Failed to create ITaskbarList4 instance. Taskbar integration disabled");
                return;
            }

            _taskbarList = taskbarList;
            var hResult = _taskbarList.HrInit();
            if (hResult < 0)
            {
                _logger.LogError("Failed to initialize ITaskbarList4: HRESULT 0x{HResult:X}", hResult);
                _taskbarList = null;
                return;
            }
        }
        catch (COMException ex)
        {
            _logger.LogError(ex, "COM error initializing TaskbarList4. Running on Windows Server or headless environment?");
            _taskbarList = null;
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize TaskbarList4");
            _taskbarList = null;
            return;
        }

        _wmTaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");

        _playbackService.PlaybackStateChanged += OnPlaybackStateChanged;
        _playbackService.TrackChanged += OnTrackChanged;

        _isInitialized = true;

        // Initial icon generation and button update
        _ = RefreshIconsAsync();
    }

    /// <inheritdoc />
    public void HandleWindowMessage(int msg, nint wParam, nint lParam)
    {
        // Guard against calls before initialization or after disposal
        if (!_isInitialized || _isDisposed) return;

        // Handle the TaskbarButtonCreated message to re-add buttons after explorer restarts
        if ((uint)msg == _wmTaskbarButtonCreated)
        {
            _logger.LogDebug("TaskbarButtonCreated received, reinitializing buttons");
            _buttons = null; // Force re-creation
            _ = RefreshIconsAsync();
            return;
        }

        // Handle button clicks
        if (_taskbarList is null || msg != WM_COMMAND || HIWORD(wParam) != THBN_CLICKED) return;

        switch (LOWORD(wParam))
        {
            case PreviousButtonId:
                _ = _playbackService.PreviousAsync();
                break;
            case PlayPauseButtonId:
                _ = _playbackService.PlayPauseAsync();
                break;
            case NextButtonId:
                _ = _playbackService.NextAsync();
                break;
        }
    }

    /// <inheritdoc />
    public async Task RefreshIconsAsync()
    {
        if (!_isInitialized || _isDisposed) return;

        // Atomically swap in a new CTS and cancel+dispose the old one to prevent race conditions
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _iconRefreshCts, newCts);
        if (oldCts != null)
        {
            try { oldCts.Cancel(); oldCts.Dispose(); }
            catch (ObjectDisposedException) { /* Already disposed, ignore */ }
        }

        var token = newCts.Token;

        try
        {
            // Debounce rapid calls (e.g., during theme switching)
            await Task.Delay(IconRefreshDebounceMs, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;

            // Read theme color on UI thread (Application.Current.RequestedTheme is not thread-safe)
            Color foregroundColor = default;
            await _dispatcherService.EnqueueAsync(() =>
            {
                foregroundColor = GetCurrentThemeForegroundColor();
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (token.IsCancellationRequested || _isDisposed) return;

            // Generate icons on background thread (Win2D is thread-safe)
            await Task.Run(() =>
            {
                if (token.IsCancellationRequested || _isDisposed) return;
                GenerateIconsInternal(foregroundColor);
            }, token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Icon refresh was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh taskbar icons");
        }
    }

    private void OnPlaybackStateChanged()
    {
        if (_isDisposed) return;
        // Marshal to UI thread for button updates (taskbar API requires STA)
        _dispatcherService.TryEnqueue(UpdateTaskbarButtons);
    }

    private void OnTrackChanged()
    {
        if (_isDisposed) return;
        // Marshal to UI thread for button updates (taskbar API requires STA)
        _dispatcherService.TryEnqueue(UpdateTaskbarButtons);
    }

    private void GenerateIconsInternal(Color foreground)
    {
        if (_isDisposed) return;

        nint newPrevIcon = 0, newPlayIcon = 0, newPauseIcon = 0, newNextIcon = 0;

        try
        {
            var iconSize = GetIconSizeForDpi();

            newPrevIcon = RenderGlyphToIcon(PreviousGlyph, iconSize, foreground);
            newPlayIcon = RenderGlyphToIcon(PlayGlyph, iconSize, foreground);
            newPauseIcon = RenderGlyphToIcon(PauseGlyph, iconSize, foreground);
            newNextIcon = RenderGlyphToIcon(NextGlyph, iconSize, foreground);

            // Atomically swap icons and destroy old ones
            lock (_iconLock)
            {
                var oldPrev = _prevIcon;
                var oldPlay = _playIcon;
                var oldPause = _pauseIcon;
                var oldNext = _nextIcon;

                _prevIcon = newPrevIcon;
                _playIcon = newPlayIcon;
                _pauseIcon = newPauseIcon;
                _nextIcon = newNextIcon;

                // Destroy old icons after assignment to prevent race conditions
                if (oldPrev != 0) DestroyIcon(oldPrev);
                if (oldPlay != 0) DestroyIcon(oldPlay);
                if (oldPause != 0) DestroyIcon(oldPause);
                if (oldNext != 0) DestroyIcon(oldNext);

                // Clear the new* variables so we don't accidentally destroy them in the catch block
                newPrevIcon = newPlayIcon = newPauseIcon = newNextIcon = 0;
            }

            // Initialize or update buttons (must be on UI thread for COM)
            _dispatcherService.TryEnqueue(() =>
            {
                if (_isDisposed) return;
                
                if (_buttons == null)
                {
                    InitializeButtons();
                }
                else
                {
                    UpdateTaskbarButtons();
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate taskbar icons");

            // Clean up any icons that were created before the failure
            if (newPrevIcon != 0) DestroyIcon(newPrevIcon);
            if (newPlayIcon != 0) DestroyIcon(newPlayIcon);
            if (newPauseIcon != 0) DestroyIcon(newPauseIcon);
            if (newNextIcon != 0) DestroyIcon(newNextIcon);
        }
    }

    private int GetIconSizeForDpi()
    {
        try
        {
            var dpi = _win32InteropService.GetDpiForWindow(_windowHandle);
            // Scale: 96 DPI = 16px, 144 DPI = 24px, 192 DPI = 32px
            return Math.Max(DefaultIconSize, (int)(DefaultIconSize * dpi / BaseDpi));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get DPI for window, using default icon size");
            return DefaultIconSize;
        }
    }

    /// <summary>
    ///     Gets the current theme foreground color for icon rendering.
    ///     Must be called from the UI thread.
    /// </summary>
    private static Color GetCurrentThemeForegroundColor()
    {
        return Application.Current.RequestedTheme == ApplicationTheme.Dark
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 0, 0, 0);
    }

    /// <summary>
    ///     Renders a glyph from the Segoe MDL2 Assets font to an HICON using Win2D.
    ///     This method uses GPU-accelerated rendering and does not require the UI thread.
    /// </summary>
    /// <param name="glyph">The Unicode glyph character to render.</param>
    /// <param name="size">The size of the icon in pixels.</param>
    /// <param name="foreground">The foreground color for the glyph.</param>
    /// <returns>A handle to the created icon, or IntPtr.Zero on failure.</returns>
    private nint RenderGlyphToIcon(string glyph, int size, Color foreground)
    {
        try
        {
            // Get shared GPU device (thread-safe, automatically handles device loss)
            var device = CanvasDevice.GetSharedDevice();

            // Create off-screen render target
            using var renderTarget = new CanvasRenderTarget(device, size, size, 96f);

            // Configure text format for centered icon rendering
            using var textFormat = new CanvasTextFormat
            {
                FontFamily = IconFontFamily,
                FontSize = size * FontSizeMultiplier,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };

            // Draw the glyph to the render target
            using (var ds = renderTarget.CreateDrawingSession())
            {
                ds.Clear(Color.FromArgb(0, 0, 0, 0));
                ds.DrawText(glyph, new Rect(0, 0, size, size), foreground, textFormat);
            }

            // Extract pixel data (BGRA8 format, same as RenderTargetBitmap)
            var pixels = renderTarget.GetPixelBytes();

            // Convert pixels to HICON
            return CreateIconFromPixels(pixels, size, size);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.LogError(ex, "Win2D rendering failed for glyph {Glyph}", glyph);
            return IntPtr.Zero;
        }
    }

    /// <summary>
    ///     Creates an HICON from raw BGRA8 pixel data using Win32 GDI.
    /// </summary>
    /// <param name="pixels">The pixel data in BGRA8 format.</param>
    /// <param name="width">The width of the image in pixels.</param>
    /// <param name="height">The height of the image in pixels.</param>
    /// <returns>A handle to the created icon, or IntPtr.Zero on failure.</returns>
    private nint CreateIconFromPixels(byte[] pixels, int width, int height)
    {
        // Create a DIB Section for alpha transparency
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // Negative for top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            }
        };

        var hBmColor = CreateDIBSection(IntPtr.Zero, ref bmi, DIB_RGB_COLORS, out var ppvBits, IntPtr.Zero, 0);
        if (hBmColor == IntPtr.Zero)
        {
            _logger.LogError("CreateDIBSection failed with error code: {ErrorCode}", Marshal.GetLastWin32Error());
            return IntPtr.Zero;
        }

        // Copy pixels into the DIB section
        Marshal.Copy(pixels, 0, ppvBits, pixels.Length);

        // Create mask bitmap with all bits set to 0 (fully opaque).
        // For 32-bit icons with alpha, the mask is ignored but must be valid.
        var maskSize = ((width + 31) / 32) * 4 * height; // Rows are DWORD-aligned
        var maskBits = new byte[maskSize];
        var hBmMask = CreateBitmap(width, height, 1, 1, maskBits);
        if (hBmMask == IntPtr.Zero)
        {
            _logger.LogError("CreateBitmap for mask failed with error code: {ErrorCode}", Marshal.GetLastWin32Error());
            DeleteObject(hBmColor);
            return IntPtr.Zero;
        }

        // Create the HICON
        var iconInfo = new ICONINFO
        {
            fIcon = true,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = hBmMask,
            hbmColor = hBmColor
        };

        var hIcon = CreateIconIndirect(ref iconInfo);

        // Clean up GDI objects (the icon has its own copy)
        DeleteObject(hBmColor);
        DeleteObject(hBmMask);

        return hIcon;
    }

    private void InitializeButtons()
    {
        if (_taskbarList == null) return;

        lock (_iconLock)
        {
            _buttons =
            [
                // Previous Button
                new THUMBBUTTON
                {
                    iId = PreviousButtonId,
                    dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
                    hIcon = _prevIcon,
                    szTip = "Previous",
                    dwFlags = CanGoPrevious() ? THBF.ENABLED : THBF.DISABLED
                },

                // Play/Pause Button
                new THUMBBUTTON
                {
                    iId = PlayPauseButtonId,
                    dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
                    hIcon = _playbackService.IsPlaying ? _pauseIcon : _playIcon,
                    szTip = _playbackService.IsPlaying ? "Pause" : "Play",
                    dwFlags = THBF.ENABLED
                },

                // Next Button
                new THUMBBUTTON
                {
                    iId = NextButtonId,
                    dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
                    hIcon = _nextIcon,
                    szTip = "Next",
                    dwFlags = CanGoNext() ? THBF.ENABLED : THBF.DISABLED
                }
            ];
        }

        var hResult = _taskbarList.ThumbBarAddButtons(_windowHandle, (uint)_buttons.Length, _buttons);
        if (hResult < 0)
        {
            _logger.LogError("Failed to add thumbnail buttons: HRESULT 0x{HResult:X}", hResult);
        }
        else
        {
            _logger.LogDebug("Taskbar thumbnail buttons initialized successfully");
        }
    }

    private void UpdateTaskbarButtons()
    {
        if (_taskbarList == null || _buttons == null || _isDisposed) return;

        lock (_iconLock)
        {
            // Previous button state
            _buttons[0].hIcon = _prevIcon;
            _buttons[0].dwFlags = CanGoPrevious() ? THBF.ENABLED : THBF.DISABLED;

            // Play/Pause button state and icon
            if (_playbackService.IsPlaying)
            {
                _buttons[1].hIcon = _pauseIcon;
                _buttons[1].szTip = "Pause";
            }
            else
            {
                _buttons[1].hIcon = _playIcon;
                _buttons[1].szTip = "Play";
            }
            _buttons[1].dwFlags = THBF.ENABLED;

            // Next button state
            _buttons[2].hIcon = _nextIcon;
            _buttons[2].dwFlags = CanGoNext() ? THBF.ENABLED : THBF.DISABLED;
        }

        var hResult = _taskbarList.ThumbBarUpdateButtons(_windowHandle, (uint)_buttons.Length, _buttons);
        if (hResult < 0)
        {
            _logger.LogError("Failed to update thumbnail buttons: HRESULT 0x{HResult:X}", hResult);
        }
    }

    private bool CanGoPrevious()
    {
        var queue = _playbackService.PlaybackQueue;
        if (queue == null || queue.Count == 0) return false;

        var index = _playbackService.CurrentQueueIndex;
        if (index < 0) return false;

        return index > 0 || _playbackService.CurrentRepeatMode == RepeatMode.RepeatAll;
    }

    private bool CanGoNext()
    {
        var queue = _playbackService.PlaybackQueue;
        if (queue == null || queue.Count == 0) return false;

        var index = _playbackService.CurrentQueueIndex;
        if (index < 0) return false;

        return index < queue.Count - 1 || _playbackService.CurrentRepeatMode == RepeatMode.RepeatAll;
    }

    /// <summary>
    ///     Releases unmanaged resources used by the taskbar service.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // Only unsubscribe from events if we successfully subscribed during initialization
        if (_isInitialized)
        {
            _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
            _playbackService.TrackChanged -= OnTrackChanged;
        }

        // Cancel any pending icon refresh
        var cts = _iconRefreshCts;
        _iconRefreshCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); cts.Dispose(); }
            catch (ObjectDisposedException) { /* Already disposed */ }
        }

        // Destroy all icons
        lock (_iconLock)
        {
            if (_prevIcon != 0) { DestroyIcon(_prevIcon); _prevIcon = 0; }
            if (_nextIcon != 0) { DestroyIcon(_nextIcon); _nextIcon = 0; }
            if (_playIcon != 0) { DestroyIcon(_playIcon); _playIcon = 0; }
            if (_pauseIcon != 0) { DestroyIcon(_pauseIcon); _pauseIcon = 0; }
        }

        // Release COM object
        if (_taskbarList != null)
        {
            try
            {
                Marshal.ReleaseComObject(_taskbarList);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error releasing ITaskbarList4 COM object");
            }
            _taskbarList = null;
        }

        GC.SuppressFinalize(this);
    }
}
