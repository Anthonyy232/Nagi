using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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
    private const int PreviousButtonId = 1;
    private const int PlayPauseButtonId = 2;
    private const int NextButtonId = 3;
    private const int IconRefreshDebounceMs = 100;
    private const int DefaultIconSize = 16;
    private const double BaseDpi = 96.0;
    // Windows 11+ uses Segoe Fluent Icons (filled/bolder variants).
    // Windows 10 falls back to Segoe MDL2 Assets.
    private const string PreviousGlyphFluent = "\uF8AC";
    private const string PlayGlyphFluent = "\uF5B0";
    private const string PauseGlyphFluent = "\uF8AE";
    private const string NextGlyphFluent = "\uF8AD";

    private const string PreviousGlyphMdl2 = "\uE892";
    private const string PlayGlyphMdl2 = "\uE768";
    private const string PauseGlyphMdl2 = "\uE769";
    private const string NextGlyphMdl2 = "\uE893";
    private const string FluentIconFontFamily = "Segoe Fluent Icons";
    private const string Mdl2IconFontFamily = "Segoe MDL2 Assets";

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

    private readonly Debouncer _iconRefreshDebouncer = new(TimeSpan.FromMilliseconds(IconRefreshDebounceMs));

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
            _taskbarList = (ITaskbarList4)Activator.CreateInstance(Type.GetTypeFromCLSID(TaskbarListGuid)!)!;
            Marshal.ThrowExceptionForHR(_taskbarList.HrInit());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize TaskbarList4");
            if (_taskbarList != null) Marshal.ReleaseComObject(_taskbarList);
            _taskbarList = null;
            return;
        }

        _wmTaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");

        _playbackService.PlaybackStateChanged += OnPlaybackChanged;
        _playbackService.TrackChanged += OnPlaybackChanged;
        _playbackService.QueueChanged += OnPlaybackChanged;
        _playbackService.ShuffleModeChanged += OnPlaybackChanged;
        _playbackService.RepeatModeChanged += OnPlaybackChanged;

        _isInitialized = true;
        _ = RefreshIconsAsync();
    }

    /// <inheritdoc />
    public void HandleWindowMessage(int msg, nint wParam, nint lParam)
    {
        if (!_isInitialized || _isDisposed) return;

        // Handle the TaskbarButtonCreated message to re-add buttons after explorer restarts
        if ((uint)msg == _wmTaskbarButtonCreated)
        {
            _logger.LogDebug("TaskbarButtonCreated received, reinitializing buttons");
            _buttons = null; // Force re-creation
            _ = RefreshIconsAsync();
            return;
        }
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
    public Task RefreshIconsAsync()
    {
        if (!_isInitialized || _isDisposed) return Task.CompletedTask;

        return _iconRefreshDebouncer.RunAsync(async token =>
        {
            try
            {
                var foregroundColor = await _dispatcherService.EnqueueAsync(() =>
                    token.IsCancellationRequested || _isDisposed ? default : GetCurrentThemeForegroundColor()).ConfigureAwait(false);

                // Win2D icon generation can run off the UI thread.
                await Task.Run(() =>
                {
                    if (!token.IsCancellationRequested && !_isDisposed)
                        GenerateIconsInternal(foregroundColor);
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to refresh taskbar icons");
            }
        });
    }

    private void OnPlaybackChanged()
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
            var isWin11 = _win32InteropService.IsWindows11OrNewer;
            var fontFamily = isWin11 ? FluentIconFontFamily : Mdl2IconFontFamily;
            var prevGlyph = isWin11 ? PreviousGlyphFluent : PreviousGlyphMdl2;
            var playGlyph = isWin11 ? PlayGlyphFluent : PlayGlyphMdl2;
            var pauseGlyph = isWin11 ? PauseGlyphFluent : PauseGlyphMdl2;
            var nextGlyph = isWin11 ? NextGlyphFluent : NextGlyphMdl2;

            newPrevIcon = RenderGlyphToIcon(prevGlyph, iconSize, foreground, fontFamily);
            newPlayIcon = RenderGlyphToIcon(playGlyph, iconSize, foreground, fontFamily);
            newPauseIcon = RenderGlyphToIcon(pauseGlyph, iconSize, foreground, fontFamily);
            newNextIcon = RenderGlyphToIcon(nextGlyph, iconSize, foreground, fontFamily);
            lock (_iconLock)
            {
                if (_isDisposed) return;

                var oldPrev = _prevIcon;
                var oldPlay = _playIcon;
                var oldPause = _pauseIcon;
                var oldNext = _nextIcon;

                _prevIcon = newPrevIcon;
                _playIcon = newPlayIcon;
                _pauseIcon = newPauseIcon;
                _nextIcon = newNextIcon;
                if (oldPrev != 0) DestroyIcon(oldPrev);
                if (oldPlay != 0) DestroyIcon(oldPlay);
                if (oldPause != 0) DestroyIcon(oldPause);
                if (oldNext != 0) DestroyIcon(oldNext);
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
        }
        finally
        {
            if (newPrevIcon != 0) DestroyIcon(newPrevIcon);
            if (newPlayIcon != 0) DestroyIcon(newPlayIcon);
            if (newPauseIcon != 0) DestroyIcon(newPauseIcon);
            if (newNextIcon != 0) DestroyIcon(newNextIcon);
        }
    }

    private int GetIconSizeForDpi()
    {
        var dpi = _win32InteropService.GetDpiForWindow(_windowHandle);
        return Math.Max(DefaultIconSize, (int)(DefaultIconSize * dpi / BaseDpi));
    }

    /// <summary>
    ///     Gets the app primary color for icon rendering.
    ///     Falls back to theme-based foreground if the resource is not found.
    ///     Must be called from the UI thread.
    /// </summary>
    private static Color GetCurrentThemeForegroundColor()
    {
        if (Application.Current.Resources.TryGetValue("AppPrimaryColorBrush", out var resource)
            && resource is SolidColorBrush brush)
        {
            return brush.Color;
        }
        return Application.Current.RequestedTheme == ApplicationTheme.Dark
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 0, 0, 0);
    }

    /// <summary>
    ///     Renders a glyph to an HICON using Win2D.
    ///     This method uses GPU-accelerated rendering and does not require the UI thread.
    /// </summary>
    /// <param name="glyph">The Unicode glyph character to render.</param>
    /// <param name="size">The size of the icon in pixels.</param>
    /// <param name="foreground">The foreground color for the glyph.</param>
    /// <param name="fontFamily">The font family to use for rendering.</param>
    /// <returns>A handle to the created icon, or IntPtr.Zero on failure.</returns>
    private nint RenderGlyphToIcon(string glyph, int size, Color foreground, string fontFamily)
    {
        try
        {
            // Get shared GPU device (thread-safe, automatically handles device loss)
            var device = CanvasDevice.GetSharedDevice();
            using var renderTarget = new CanvasRenderTarget(device, size, size, 96f);
            using var textFormat = new CanvasTextFormat
            {
                FontFamily = fontFamily,
                FontSize = size,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
            using (var ds = renderTarget.CreateDrawingSession())
            {
                ds.Clear(Color.FromArgb(0, 0, 0, 0));
                ds.DrawText(glyph, new Rect(0, 0, size, size), foreground, textFormat);
            }

            // Extract pixel data (BGRA8 format, same as RenderTargetBitmap)
            var pixels = renderTarget.GetPixelBytes();
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
                new THUMBBUTTON
                {
                    iId = PreviousButtonId,
                    dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
                    hIcon = _prevIcon,
                    szTip = Resources.Strings.Taskbar_Tooltip_Previous,
                    dwFlags = CanGoPrevious() ? THBF.ENABLED : THBF.DISABLED
                },
                new THUMBBUTTON
                {
                    iId = PlayPauseButtonId,
                    dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
                    hIcon = _playbackService.IsPlaying ? _pauseIcon : _playIcon,
                    szTip = _playbackService.IsPlaying ? Resources.Strings.Taskbar_Tooltip_Pause : Resources.Strings.Taskbar_Tooltip_Play,
                    dwFlags = THBF.ENABLED
                },
                new THUMBBUTTON
                {
                    iId = NextButtonId,
                    dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
                    hIcon = _nextIcon,
                    szTip = Resources.Strings.Taskbar_Tooltip_Next,
                    dwFlags = CanGoNext() ? THBF.ENABLED : THBF.DISABLED
                }
            ];
            var hResult = _taskbarList.ThumbBarAddButtons(_windowHandle, (uint)_buttons.Length, _buttons);
            if (hResult < 0)
            {
                _logger.LogError("Failed to add thumbnail buttons: HRESULT 0x{HResult:X}", hResult);
            }
        }
    }

    private void UpdateTaskbarButtons()
    {
        if (_taskbarList == null || _buttons == null || _isDisposed) return;

        lock (_iconLock)
        {
            _buttons[0].hIcon = _prevIcon;
            _buttons[0].dwFlags = CanGoPrevious() ? THBF.ENABLED : THBF.DISABLED;
            if (_playbackService.IsPlaying)
            {
                _buttons[1].hIcon = _pauseIcon;
                _buttons[1].szTip = Resources.Strings.Taskbar_Tooltip_Pause;
            }
            else
            {
                _buttons[1].hIcon = _playIcon;
                _buttons[1].szTip = Resources.Strings.Taskbar_Tooltip_Play;
            }
            _buttons[1].dwFlags = THBF.ENABLED;
            _buttons[2].hIcon = _nextIcon;
            _buttons[2].dwFlags = CanGoNext() ? THBF.ENABLED : THBF.DISABLED;
            var hResult = _taskbarList.ThumbBarUpdateButtons(_windowHandle, (uint)_buttons.Length, _buttons);
            if (hResult < 0)
            {
                _logger.LogError("Failed to update thumbnail buttons: HRESULT 0x{HResult:X}", hResult);
            }
        }
    }

    private bool CanGoPrevious()
    {
        var index = _playbackService.IsShuffleEnabled
            ? _playbackService.CurrentShuffledIndex
            : _playbackService.CurrentQueueIndex;
        return _playbackService.PlaybackQueue.Count > 0 &&
               (_playbackService.CurrentTrack != null || index > 0 ||
                _playbackService.CurrentRepeatMode != RepeatMode.Off);
    }

    private bool CanGoNext()
    {
        var queue = _playbackService.IsShuffleEnabled
            ? _playbackService.ShuffledQueue
            : _playbackService.PlaybackQueue;
        var index = _playbackService.IsShuffleEnabled
            ? _playbackService.CurrentShuffledIndex
            : _playbackService.CurrentQueueIndex;
        return queue.Count > 0 &&
               (index < queue.Count - 1 || _playbackService.CurrentRepeatMode != RepeatMode.Off);
    }

    /// <summary>
    ///     Releases unmanaged resources used by the taskbar service.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (_isInitialized)
        {
            _playbackService.PlaybackStateChanged -= OnPlaybackChanged;
            _playbackService.TrackChanged -= OnPlaybackChanged;
            _playbackService.QueueChanged -= OnPlaybackChanged;
            _playbackService.ShuffleModeChanged -= OnPlaybackChanged;
            _playbackService.RepeatModeChanged -= OnPlaybackChanged;
        }

        _iconRefreshDebouncer.Dispose();
        lock (_iconLock)
        {
            if (_prevIcon != 0) { DestroyIcon(_prevIcon); _prevIcon = 0; }
            if (_nextIcon != 0) { DestroyIcon(_nextIcon); _nextIcon = 0; }
            if (_playIcon != 0) { DestroyIcon(_playIcon); _playIcon = 0; }
            if (_pauseIcon != 0) { DestroyIcon(_pauseIcon); _pauseIcon = 0; }
        }
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

    }
}
