using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Resources;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;

namespace Nagi.WinUI.ViewModels;

public partial class PlayerViewModel
{
    private readonly IUIService _uiService;
    private Timer? _stopTimer;
    private bool _checkingStopSchedule;

    [ObservableProperty]
    public partial bool IsStopScheduled { get; set; }

    [ObservableProperty]
    public partial string StopScheduleText { get; set; } = string.Empty;

    private void InitializeListeningEvents()
    {
        _playbackService.StopScheduleChanged += OnStopScheduleChanged;
        OnStopScheduleChanged();
    }

    private void CleanupListening()
    {
        _stopTimer?.Dispose();
        _playbackService.StopScheduleChanged -= OnStopScheduleChanged;
    }

    [RelayCommand]
    private Task PlayQueueMixAsync(ShuffleMode mode) => RunListeningActionAsync(async () =>
    {
        if (!await _playbackService.PlayMixAsync(_playbackService.PlaybackQueue, mode))
            await _uiService.ShowMessageDialogAsync(Strings.Listening_NoMixTitle, Strings.Listening_NoMixDescription);
    });

    [RelayCommand]
    private Task PlayQueueForAsync() => RunListeningActionAsync(async () =>
    {
        var options = await _uiService.ShowPlayForDialogAsync();
        if (options is not null && !await _playbackService.PlayMixAsync(_playbackService.PlaybackQueue, options.Mode, TimeSpan.FromMinutes(options.Minutes), avoidRecentDays: options.AvoidRecentDays))
            await _uiService.ShowMessageDialogAsync(Strings.Listening_NoMixTitle, Strings.Listening_NoMixDescription);
    });

    [RelayCommand]
    private Task SetCustomSleepTimerAsync() => RunListeningActionAsync(async () =>
    {
        var options = await _uiService.ShowSleepTimerDialogAsync();
        if (options is { } timer)
            _playbackService.ScheduleStop(PlaybackStopMode.Timer, TimeSpan.FromMinutes(timer.Minutes), timer.FinishTrack);
    });

    private async Task RunListeningActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listening action failed");
            if (!_isCleanedUp) await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, Strings.Listening_ActionError);
        }
    }

    private bool CanStopAfterTrack() => CurrentPlayingTrack is not null;

    [RelayCommand(CanExecute = nameof(CanStopAfterTrack))]
    private void StopAfterTrack() => _playbackService.ScheduleStop(PlaybackStopMode.AfterTrack);

    [RelayCommand(CanExecute = nameof(CanClearQueue))]
    private void StopAtQueueEnd() => _playbackService.ScheduleStop(PlaybackStopMode.EndOfQueue);

    [RelayCommand]
    private void SetSleepTimer(int minutes) =>
        _playbackService.ScheduleStop(PlaybackStopMode.Timer, TimeSpan.FromMinutes(minutes));

    [RelayCommand]
    private void CancelScheduledStop() => _playbackService.ScheduleStop(PlaybackStopMode.Off);

    private void OnStopScheduleChanged() => RunOnUIThread(() =>
    {
        if (_playbackService.StopMode == PlaybackStopMode.Timer)
        {
            _stopTimer ??= new Timer(state => RunOnUIThread(() => _ = CheckStopScheduleAsync()), null, Timeout.Infinite, Timeout.Infinite);
            _stopTimer.Change(1000, 1000);
        }
        else _stopTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        UpdateStopScheduleText();
    });

    private async Task CheckStopScheduleAsync()
    {
        if (_checkingStopSchedule || _isCleanedUp) return;
        _checkingStopSchedule = true;
        try
        {
            await _playbackService.CheckStopScheduleAsync();
            UpdateStopScheduleText();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to apply scheduled playback stop"); }
        finally { _checkingStopSchedule = false; }
    }

    private void UpdateStopScheduleText()
    {
        IsStopScheduled = _playbackService.StopMode != PlaybackStopMode.Off;
        StopScheduleText = _playbackService.StopMode switch
        {
            PlaybackStopMode.AfterTrack => Strings.Listening_StopAfterTrack,
            PlaybackStopMode.EndOfQueue => Strings.Listening_StopAtQueueEnd,
            PlaybackStopMode.Timer => string.Format(Strings.Listening_TimerRemaining,
                Converters.TimeSpanToTimeStringConverter.Format(TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((_playbackService.StopDeadline!.Value - DateTimeOffset.UtcNow).TotalSeconds))))),
            _ => string.Empty
        };
    }
}
