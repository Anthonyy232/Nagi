using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Nagi.WinUI.ViewModels;

/// <summary>
/// Manages the state and logic for the LyricsPage. It interfaces with playback
/// services to get song information and lyrics, and provides properties for data
/// binding to the view.
/// </summary>
public partial class LyricsPageViewModel : ObservableObject, IDisposable {
    private readonly IMusicPlaybackService _playbackService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILrcService _lrcService;
    private bool _isDisposed;
    private ParsedLrc? _parsedLrc;

    // A hint to optimize searching for the current line in the lyrics list.
    private int _lrcSearchHint;

    // A small time offset to rewind before a line when seeking.
    private readonly TimeSpan _seekTimeOffset = TimeSpan.FromSeconds(0.2);

    // These fields implement an optimistic update mechanism. When a user seeks to a new
    // line, the UI is updated instantly. This "optimistic" line is held for a brief
    // grace period to prevent flickering caused by latency from the playback service.
    private LyricLine? _optimisticallySetLine;
    private DateTime _optimisticSetTimestamp;
    private static readonly TimeSpan OptimisticUpdateGracePeriod = TimeSpan.FromSeconds(2);

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
    /// Seeks the playback position to the start of the specified lyric line.
    /// </summary>
    [RelayCommand]
    public async Task SeekToLineAsync(LyricLine? line) {
        if (line is null) return;

        var targetTime = line.StartTime - _seekTimeOffset;
        if (targetTime < TimeSpan.Zero) targetTime = TimeSpan.Zero;

        // Optimistically set the current line in the UI to provide instant feedback.
        _optimisticallySetLine = line;
        _optimisticSetTimestamp = DateTime.UtcNow;
        UpdateCurrentLineFromPosition(targetTime);

        // Command the playback service to perform the actual seek.
        await _playbackService.SeekAsync(targetTime);
    }

    private void OnPlaybackServicePositionChanged() {
        UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
    }

    /// <summary>
    /// Updates the current lyric line based on the given playback position.
    /// </summary>
    private void UpdateCurrentLineFromPosition(TimeSpan position) {
        if (_parsedLrc is null || !HasLyrics) return;

        _dispatcherService.TryEnqueue(() => {
            CurrentPosition = position;
            var newCurrentLine = _lrcService.GetCurrentLine(_parsedLrc, position, ref _lrcSearchHint);

            // If an optimistic update was performed recently, honor it to prevent UI flicker
            // from playback service latency.
            if (_optimisticallySetLine != null && (DateTime.UtcNow - _optimisticSetTimestamp) < OptimisticUpdateGracePeriod) {
                // If the service reports a different line than the one the user clicked,
                // trust the user's action and stick to the optimistically set line.
                if (!ReferenceEquals(newCurrentLine, _optimisticallySetLine)) {
                    newCurrentLine = _optimisticallySetLine;
                }
            }
            else {
                // The grace period has passed, so clear the optimistic line.
                _optimisticallySetLine = null;
            }

            // Update the property only if the line has actually changed.
            if (!ReferenceEquals(newCurrentLine, CurrentLine)) {
                if (CurrentLine != null) CurrentLine.IsActive = false;
                if (newCurrentLine != null) newCurrentLine.IsActive = true;
                SetProperty(ref _currentLine, newCurrentLine, nameof(CurrentLine));
            }
        });
    }

    /// <summary>
    /// Loads lyrics and resets state for a new song.
    /// </summary>
    private async void UpdateForTrack(Song? song) {
        await _dispatcherService.EnqueueAsync(async () => {
            // Reset all state for the new track.
            LyricLines.Clear();
            CurrentLine = null;
            _parsedLrc = null;
            _lrcSearchHint = 0;
            _optimisticallySetLine = null;
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
                UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
            }
        });
    }

    private void OnPlaybackServiceTrackChanged() => UpdateForTrack(_playbackService.CurrentTrack);
    private void OnPlaybackServiceDurationChanged() => _dispatcherService.TryEnqueue(() => { SongDuration = _playbackService.Duration; });
    private void OnPlaybackServicePlaybackStateChanged() => _dispatcherService.TryEnqueue(() => { IsPlaying = _playbackService.IsPlaying; });

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