using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Player;

public partial class PlayerViewModel
{
    private ITimer? _countdownTimer;
    public RecentHistoryViewModel History { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteActionText))]
    public partial bool IsCurrentTrackFavorite { get; set; }

    public string FavoriteActionText => IsCurrentTrackFavorite ? Strings.Listening_RemoveFavorite : Strings.Listening_AddFavorite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListeningPaneTitle), nameof(ListeningPaneToggleText), nameof(ListeningPaneToggleIcon))]
    public partial bool IsHistoryVisible { get; set; }

    public string ListeningPaneTitle => IsHistoryVisible ? Strings.Listening_RecentlyPlayed : Strings.Player_UpNextHeader;

    /// <summary>Names the pane the toggle switches to.</summary>
    public string ListeningPaneToggleText => IsHistoryVisible ? Strings.Player_UpNextHeader : Strings.Listening_RecentlyPlayed;

    public AppIcon ListeningPaneToggleIcon => IsHistoryVisible ? AppIcon.Queue : AppIcon.History;

    public IReadOnlyList<PlaybackRateOption> PlaybackRateOptions { get; } =
        new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 }.Select(rate => new PlaybackRateOption(rate,
            rate == 1.0 ? Strings.Listening_PlaybackSpeedNormal : ResourceFormatter.Format(Strings.Listening_PlaybackSpeedFormat, rate)))
        .ToList();

    public IReadOnlyList<SleepTimerOption> SleepTimerOptions { get; } =
    [
        new(15, Strings.Listening_Timer15),
        new(30, Strings.Listening_Timer30),
        new(60, Strings.Listening_Timer60)
    ];

    [ObservableProperty]
    public partial bool IsStopScheduled { get; set; }

    [ObservableProperty]
    public partial string StopScheduleText { get; set; } = string.Empty;

    private void InitializeListeningEvents()
    {
        _playbackService.StopScheduleChanged += OnStopScheduleChanged;
        _libraryNotifier.Changed += OnFavoriteLibraryChanged;
        _playbackService.TrackChanged += OnHistoryTrackChanged;
        OnStopScheduleChanged();
    }

    private void CleanupListening()
    {
        _countdownTimer?.Dispose();
        History.Dispose();
        _playbackService.StopScheduleChanged -= OnStopScheduleChanged;
        _libraryNotifier.Changed -= OnFavoriteLibraryChanged;
        _playbackService.TrackChanged -= OnHistoryTrackChanged;
    }

    partial void OnIsHistoryVisibleChanged(bool value) => UpdateActivePane();

    private void OnHistoryTrackChanged() => RunOnUIThread(() =>
    {
        if (IsQueuePanelOpen && IsHistoryVisible) _ = History.RefreshAsync();
    });

    private void OnFavoriteLibraryChanged(object? sender, LibraryChange change)
    {
        if (!change.AffectsFavorites) return;
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
    private Task ToggleFavoriteAsync() => RunActionAsync(async () =>
    {
        var current = CurrentPlayingTrack;
        if (current is null) return;
        var loved = !IsCurrentTrackFavorite;
        if (await Task.Run(() => _listening.SetSongsLovedAsync([current.Id], loved)) && ReferenceEquals(current, CurrentPlayingTrack))
        {
            current.IsLoved = loved;
            IsCurrentTrackFavorite = loved;
        }
    });

    /// <summary>
    ///     What the favorite shortcut does: <paramref name="toggleSelectedFavorite" /> for the active list's selection
    ///     when it can run, otherwise favoriting the playing track when that list has nothing selected.
    /// </summary>
    /// <returns>The command to run, or null when the shortcut does nothing.</returns>
    public IAsyncRelayCommand? GetFavoriteShortcutCommand(IAsyncRelayCommand? toggleSelectedFavorite, bool hasSelectedSongs)
    {
        if (toggleSelectedFavorite?.CanExecute(null) == true) return toggleSelectedFavorite;
        if (!hasSelectedSongs && ToggleFavoriteCommand.CanExecute(null)) return ToggleFavoriteCommand;
        return null;
    }

    [RelayCommand]
    private Task PlayQueueEntryAsync(QueueEntry entry) => SidebarQueue.PlayAsync(entry);

    [RelayCommand]
    private void MoveQueueEntryUp(QueueEntry entry) => SidebarQueue.Move(entry, -1);

    [RelayCommand]
    private void MoveQueueEntryDown(QueueEntry entry) => SidebarQueue.Move(entry, 1);

    [RelayCommand]
    private Task RemoveQueueEntryAsync(QueueEntry entry) => SidebarQueue.RemoveAsync(entry);

    [RelayCommand]
    private void ReloadQueue() => SidebarQueue.Refresh(reloadMetadata: true);

    [RelayCommand]
    private Task PlayHistoryNextAsync(RecentListenEntry? entry) => entry is null ? Task.CompletedTask : RunActionAsync(() => _playbackService.PlayNextAsync([entry.Listen.SongId]));

    [RelayCommand]
    private Task PlayHistoryNowAsync(RecentListenEntry? entry) => RunActionAsync(async () =>
    {
        if (entry is null) return;
        await _playbackService.PlayNextAsync([entry.Listen.SongId]);
        await _playbackService.PlayQueueItemAsync(entry.Listen.SongId);
    });

    [RelayCommand]
    private Task SetCustomSleepTimerAsync() => RunActionAsync(async () =>
    {
        var options = await _uiService.ShowSleepTimerDialogAsync();
        if (options is { } timer)
            _playbackService.ScheduleStop(PlaybackStopMode.Timer, TimeSpan.FromMinutes(timer.Minutes), timer.FinishTrack);
    });

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
        // Only counts down the shown time; the playback service stops playback itself.
        _countdownTimer?.Dispose();
        _countdownTimer = _playbackService.StopMode == PlaybackStopMode.Timer
            ? _timeProvider.CreateTimer(_ => RunOnUIThread(UpdateStopScheduleText), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
            : null;
        UpdateStopScheduleText();
    });

    private void UpdateStopScheduleText()
    {
        IsStopScheduled = _playbackService.StopMode != PlaybackStopMode.Off;
        StopScheduleText = _playbackService.StopMode switch
        {
            PlaybackStopMode.AfterTrack => Strings.Listening_StopAfterTrack,
            PlaybackStopMode.EndOfQueue => Strings.Listening_StopAtQueueEnd,
            PlaybackStopMode.Timer => ResourceFormatter.Format(Strings.Listening_TimerRemaining, DurationFormatter.Format(
                TimeSpan.FromSeconds(Math.Ceiling(_playbackService.StopTimeRemaining.GetValueOrDefault().TotalSeconds)))),
            _ => string.Empty
        };
    }
}

public record PlaybackRateOption(double Rate, string DisplayName);

public record SleepTimerOption(int Minutes, string DisplayName);
