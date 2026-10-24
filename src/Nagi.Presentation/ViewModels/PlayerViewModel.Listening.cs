using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

public partial class PlayerViewModel
{
    private readonly IUIService _uiService;
    private Timer? _stopTimer;
    private bool _checkingStopSchedule;
    public RecentHistoryViewModel History { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteActionText))]
    public partial bool IsCurrentTrackFavorite { get; set; }

    public string FavoriteActionText => IsCurrentTrackFavorite ? Strings.Listening_RemoveFavorite : Strings.Listening_AddFavorite;

    [ObservableProperty]
    public partial bool IsHistoryVisible { get; set; }

    [ObservableProperty]
    public partial bool IsStopScheduled { get; set; }

    [ObservableProperty]
    public partial string StopScheduleText { get; set; } = string.Empty;

    private void InitializeListeningEvents()
    {
        _playbackService.StopScheduleChanged += OnStopScheduleChanged;
        _libraryScanner.LibraryContentChanged += OnFavoriteLibraryChanged;
        _playbackService.TrackChanged += OnHistoryTrackChanged;
        OnStopScheduleChanged();
    }

    private void CleanupListening()
    {
        _stopTimer?.Dispose();
        History.Dispose();
        _playbackService.StopScheduleChanged -= OnStopScheduleChanged;
        _libraryScanner.LibraryContentChanged -= OnFavoriteLibraryChanged;
        _playbackService.TrackChanged -= OnHistoryTrackChanged;
    }

    partial void OnIsHistoryVisibleChanged(bool value)
    {
        SidebarQueue.SetActive(IsQueuePanelOpen && !value);
        History.SetActive(IsQueuePanelOpen && value);
    }

    private void OnHistoryTrackChanged() => RunOnUIThread(() =>
    {
        if (IsQueuePanelOpen && IsHistoryVisible) _ = History.RefreshAsync();
    });

    private void OnFavoriteLibraryChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        if (e.ChangeType != LibraryChangeType.SongsUpdated) return;
        RunOnUIThread(() => _ = RefreshFavoriteAsync());
    }

    private async Task RefreshFavoriteAsync()
    {
        var current = CurrentPlayingTrack;
        if (current is null) return;
        try
        {
            var song = await _libraryReader.GetSongByIdAsync(current.Id);
            if (!_isCleanedUp && ReferenceEquals(current, CurrentPlayingTrack) && song is not null)
            {
                current.IsLoved = song.IsLoved;
                IsCurrentTrackFavorite = song.IsLoved;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to refresh current track favorite"); }
    }

    private bool CanFavorite() => CurrentPlayingTrack is { FolderId: var folderId } && folderId != Guid.Empty;

    [RelayCommand(CanExecute = nameof(CanFavorite))]
    private Task ToggleFavoriteAsync() => RunListeningActionAsync(async () =>
    {
        var current = CurrentPlayingTrack;
        if (current is null) return;
        var loved = !IsCurrentTrackFavorite;
        if (await Task.Run(() => _libraryWriter.SetSongsLovedAsync([current.Id], loved)) && ReferenceEquals(current, CurrentPlayingTrack))
        {
            current.IsLoved = loved;
            IsCurrentTrackFavorite = loved;
        }
    });

    [RelayCommand]
    private Task PlayHistoryNextAsync(RecentListenEntry? entry) => entry is null ? Task.CompletedTask : RunListeningActionAsync(() => _playbackService.PlayNextAsync(entry.Listen.SongId));

    [RelayCommand]
    private Task PlayHistoryNowAsync(RecentListenEntry? entry) => RunListeningActionAsync(async () =>
    {
        if (entry is null) return;
        await _playbackService.PlayNextAsync(entry.Listen.SongId);
        var queue = _playbackService.PlaybackQueue;
        for (var index = 0; index < queue.Count; index++)
        {
            if (queue[index] != entry.Listen.SongId) continue;
            await _playbackService.PlayQueueItemAsync(index);
            break;
        }
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
                DurationFormatter.Format(TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((_playbackService.StopDeadline!.Value - DateTimeOffset.UtcNow).TotalSeconds))))),
            _ => string.Empty
        };
    }

}
