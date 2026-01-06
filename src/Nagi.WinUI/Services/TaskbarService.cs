using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;
using static Nagi.WinUI.Helpers.TaskbarNativeMethods;

namespace Nagi.WinUI.Services.Implementations;

public class TaskbarService : ITaskbarService, IDisposable
{
    private const int PREVIOUS_BUTTON_ID = 1;
    private const int PLAY_PAUSE_BUTTON_ID = 2;
    private const int NEXT_BUTTON_ID = 3;

    private readonly ILogger<TaskbarService> _logger;
    private readonly IMusicPlaybackService _playbackService;


    private ITaskbarList4? _taskbarList;
    private nint _windowHandle;
    private bool _isDisposed;
    private uint _wmTaskbarButtonCreated;

    private THUMBBUTTON[]? _buttons;
    private nint _prevIcon;
    private nint _nextIcon;
    private nint _playIcon;
    private nint _pauseIcon;

    public TaskbarService(
        ILogger<TaskbarService> logger,
        IMusicPlaybackService playbackService)
    {
        _logger = logger;
        _playbackService = playbackService;
    }

    public void Initialize(nint windowHandle)
    {
        _windowHandle = windowHandle;

        try
        {
            _taskbarList = (ITaskbarList4)Activator.CreateInstance(Type.GetTypeFromCLSID(TaskbarListGuid))!;
            _taskbarList.HrInit();
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

        UpdateTaskbarButtons();
    }

    public void HandleWindowMessage(int msg, nint wParam, nint lParam)
    {
        if (msg == _wmTaskbarButtonCreated)
        {
            if (_buttons != null) InitializeButtons();
            return;
        }

        if (_taskbarList is null || msg != WM_COMMAND || HIWORD(wParam) != THBN_CLICKED) return;

        switch (LOWORD(wParam))
        {
            case PREVIOUS_BUTTON_ID:
                _playbackService.PreviousAsync();
                break;
            case PLAY_PAUSE_BUTTON_ID:
                _playbackService.PlayPauseAsync();
                break;
            case NEXT_BUTTON_ID:
                _playbackService.NextAsync();
                break;
        }
    }

    private void OnPlaybackStateChanged() => UpdateTaskbarButtons();
    private void OnTrackChanged() => UpdateTaskbarButtons();

    public void UpdateIcons(nint previousIcon, nint playIcon, nint pauseIcon, nint nextIcon)
    {
        DestroyIcon(_prevIcon);
        DestroyIcon(_playIcon);
        DestroyIcon(_pauseIcon);
        DestroyIcon(_nextIcon);

        _prevIcon = previousIcon;
        _playIcon = playIcon;
        _pauseIcon = pauseIcon;
        _nextIcon = nextIcon;

        if (_buttons == null)
        {
            InitializeButtons();
        }
        else
        {
            _buttons[0].hIcon = _prevIcon;
            _buttons[1].hIcon = _playbackService.IsPlaying ? _pauseIcon : _playIcon;
            _buttons[2].hIcon = _nextIcon;
            UpdateTaskbarButtons();
        }
    }

    private void InitializeButtons()
    {
        _buttons = new THUMBBUTTON[3];

        // Previous Button
        _buttons[0] = new THUMBBUTTON
        {
            iId = PREVIOUS_BUTTON_ID,
            dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
            hIcon = _prevIcon,
            szTip = "Previous"
        };

        // Play/Pause Button
        _buttons[1] = new THUMBBUTTON
        {
            iId = PLAY_PAUSE_BUTTON_ID,
            dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
            hIcon = _playIcon,
            szTip = "Play"
        };

        // Next Button
        _buttons[2] = new THUMBBUTTON
        {
            iId = NEXT_BUTTON_ID,
            dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
            hIcon = _nextIcon,
            szTip = "Next"
        };

        var hResult = _taskbarList?.ThumbBarAddButtons(_windowHandle, (uint)_buttons.Length, _buttons) ?? 1;
        if (hResult < 0)
        {
            _logger.LogError("Failed to add thumbnail buttons: HRESULT 0x{HResult:X}", hResult);
        }
    }

    private void UpdateTaskbarButtons()
    {
        if (_taskbarList == null || _buttons == null) return;

        // Previous button state
        var canGoPrevious = _playbackService.CurrentQueueIndex > 0 || _playbackService.CurrentRepeatMode == Core.Services.Data.RepeatMode.RepeatAll;
        _buttons[0].dwFlags = canGoPrevious ? THBF.ENABLED : THBF.DISABLED;

        // Play/Pause button state
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
        var canGoNext = _playbackService.CurrentQueueIndex < _playbackService.PlaybackQueue.Count - 1 || _playbackService.CurrentRepeatMode == Core.Services.Data.RepeatMode.RepeatAll;
        _buttons[2].dwFlags = canGoNext ? THBF.ENABLED : THBF.DISABLED;
        
        var hResult = _taskbarList.ThumbBarUpdateButtons(_windowHandle, (uint)_buttons.Length, _buttons);
        if (hResult < 0)
        {
            _logger.LogError("Failed to update thumbnail buttons: HRESULT 0x{HResult:X}", hResult);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _playbackService.TrackChanged -= OnTrackChanged;

        DestroyIcon(_prevIcon);
        DestroyIcon(_nextIcon);
        DestroyIcon(_playIcon);
        DestroyIcon(_pauseIcon);

        if (_taskbarList != null)
        {
            Marshal.ReleaseComObject(_taskbarList);
        }
        
        GC.SuppressFinalize(this);
    }
}