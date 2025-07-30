using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;
using System;
using System.Collections.ObjectModel;

namespace Nagi.WinUI.ViewModels;

/// <summary>
/// Provides data, logic, and state management for the LyricsPage.
/// </summary>
public partial class LyricsPageViewModel : ObservableObject, IDisposable {
    private readonly IMusicPlaybackService _playbackService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILrcService _lrcService;

    private bool _isDisposed;
    private ParsedLrc? _parsedLrc;
    private int _lrcSearchHint;

    [ObservableProperty]
    private string _songTitle = "No song selected";

    [ObservableProperty]
    private bool _hasLyrics;

    [ObservableProperty]
    private LyricLine? _currentLine;

    [ObservableProperty]
    private TimeSpan _songDuration;

    [ObservableProperty]
    private TimeSpan _currentPosition;

    [ObservableProperty]
    private bool _isPlaying;

    public ObservableCollection<LyricLine> LyricLines { get; } = new();

    public LyricsPageViewModel(
        IMusicPlaybackService playbackService,
        IDispatcherService dispatcherService,
        ILrcService lrcService) {
        _playbackService = playbackService;
        _dispatcherService = dispatcherService;
        _lrcService = lrcService;

        _playbackService.TrackChanged += OnPlaybackServiceTrackChanged;
        _playbackService.PositionChanged += OnPlaybackServicePositionChanged;
        _playbackService.DurationChanged += OnPlaybackServiceDurationChanged;
        _playbackService.PlaybackStateChanged += OnPlaybackServicePlaybackStateChanged;

        // Initialize state with the currently playing track, if any.
        UpdateForTrack(_playbackService.CurrentTrack);
        IsPlaying = _playbackService.IsPlaying;
    }

    /// <summary>
    /// Updates the ViewModel's state to reflect a new song.
    /// </summary>
    private async void UpdateForTrack(Song? song) {
        // Ensure all state updates occur on the UI thread.
        await _dispatcherService.EnqueueAsync(async () => {
            LyricLines.Clear();
            CurrentLine = null;
            _parsedLrc = null;
            _lrcSearchHint = 0;
            SongDuration = TimeSpan.Zero;
            CurrentPosition = TimeSpan.Zero;

            if (song is null) {
                SongTitle = "No song selected";
                HasLyrics = false;
                return;
            }

            SongTitle = !string.IsNullOrWhiteSpace(song.Artist?.Name)
                ? $"{song.Title} by {song.Artist.Name}"
                : song.Title;

            SongDuration = _playbackService.Duration;
            _parsedLrc = await _lrcService.GetLyricsAsync(song);

            if (_parsedLrc is null || _parsedLrc.IsEmpty) {
                HasLyrics = false;
            }
            else {
                foreach (var line in _parsedLrc.Lines) {
                    LyricLines.Add(line);
                }

                HasLyrics = true;
                // Manually trigger a position update to set the initial lyric line.
                OnPlaybackServicePositionChanged();
            }
        });
    }

    /// <summary>
    /// Handles the TrackChanged event from the playback service.
    /// </summary>
    private void OnPlaybackServiceTrackChanged() {
        UpdateForTrack(_playbackService.CurrentTrack);
    }

    /// <summary>
    /// Handles the PositionChanged event from the playback service, updating the current lyric line.
    /// </summary>
    private void OnPlaybackServicePositionChanged() {
        if (_parsedLrc is null || !HasLyrics) return;

        var position = _playbackService.CurrentPosition;

        _dispatcherService.TryEnqueue(() => {
            CurrentPosition = position;

            // Use the LRC service to efficiently find the current line.
            var newCurrentLine = _lrcService.GetCurrentLine(_parsedLrc, position, ref _lrcSearchHint);

            // Only update properties if the line has actually changed to prevent unnecessary UI churn.
            if (!ReferenceEquals(newCurrentLine, CurrentLine)) {
                if (CurrentLine != null) CurrentLine.IsActive = false;
                if (newCurrentLine != null) newCurrentLine.IsActive = true;

                // Manually call SetProperty to trigger the PropertyChanged event for CurrentLine.
                SetProperty(ref _currentLine, newCurrentLine, nameof(CurrentLine));
            }
        });
    }

    /// <summary>
    /// Handles the DurationChanged event from the playback service.
    /// </summary>
    private void OnPlaybackServiceDurationChanged() {
        _dispatcherService.TryEnqueue(() => { SongDuration = _playbackService.Duration; });
    }

    /// <summary>
    /// Handles the PlaybackStateChanged event from the playback service.
    /// </summary>
    private void OnPlaybackServicePlaybackStateChanged() {
        _dispatcherService.TryEnqueue(() => { IsPlaying = _playbackService.IsPlaying; });
    }

    /// <summary>
    /// Cleans up resources and unsubscribes from events to prevent memory leaks.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        _playbackService.TrackChanged -= OnPlaybackServiceTrackChanged;
        _playbackService.PositionChanged -= OnPlaybackServicePositionChanged;
        _playbackService.DurationChanged -= OnPlaybackServiceDurationChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackServicePlaybackStateChanged;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}