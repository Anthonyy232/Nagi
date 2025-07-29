using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;
using System;
using System.Collections.ObjectModel;

namespace Nagi.WinUI.ViewModels;

/// <summary>
/// Provides data and logic for the LyricsPage.
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

    public ObservableCollection<LyricLine> LyricLines { get; } = new();

    [ObservableProperty]
    private LyricLine? _currentLine;

    [ObservableProperty]
    private TimeSpan _songDuration;

    [ObservableProperty]
    private TimeSpan _currentPosition;

    [ObservableProperty]
    private bool _isPlaying;

    public LyricsPageViewModel(
        IMusicPlaybackService playbackService,
        IDispatcherService dispatcherService,
        ILrcService lrcService) {
        _playbackService = playbackService;
        _dispatcherService = dispatcherService;
        _lrcService = lrcService;

        _playbackService.TrackChanged += OnPlaybackService_TrackChanged;
        _playbackService.PositionChanged += OnPlaybackService_PositionChanged;
        _playbackService.DurationChanged += OnPlaybackService_DurationChanged;
        _playbackService.PlaybackStateChanged += OnPlaybackService_PlaybackStateChanged;

        UpdateForTrack(_playbackService.CurrentTrack);
        IsPlaying = _playbackService.IsPlaying;
    }

    /// <summary>
    /// Updates the ViewModel's state for a new song.
    /// </summary>
    private async void UpdateForTrack(Song? song) {
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
                OnPlaybackService_PositionChanged();
            }
        });
    }

    /// <summary>
    /// Handles the TrackChanged event from the playback service.
    /// </summary>
    private void OnPlaybackService_TrackChanged() {
        UpdateForTrack(_playbackService.CurrentTrack);
    }

    /// <summary>
    /// Handles the PositionChanged event from the playback service.
    /// </summary>
    private void OnPlaybackService_PositionChanged() {
        if (_parsedLrc is null || !HasLyrics) return;

        var position = _playbackService.CurrentPosition;

        _dispatcherService.TryEnqueue(() => {
            CurrentPosition = position;

            var newCurrentLine = _lrcService.GetCurrentLine(_parsedLrc, position, ref _lrcSearchHint);

            if (!ReferenceEquals(newCurrentLine, CurrentLine)) {
                if (CurrentLine != null) CurrentLine.IsActive = false;
                if (newCurrentLine != null) newCurrentLine.IsActive = true;
                SetProperty(ref _currentLine, newCurrentLine, nameof(CurrentLine));
            }
        });
    }

    /// <summary>
    /// Handles the DurationChanged event from the playback service.
    /// </summary>
    private void OnPlaybackService_DurationChanged() {
        _dispatcherService.TryEnqueue(() => { SongDuration = _playbackService.Duration; });
    }

    /// <summary>
    /// Handles the PlaybackStateChanged event from the playback service.
    /// </summary>
    private void OnPlaybackService_PlaybackStateChanged() {
        _dispatcherService.TryEnqueue(() => { IsPlaying = _playbackService.IsPlaying; });
    }

    /// <summary>
    /// Cleans up resources and unsubscribes from events.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        _playbackService.TrackChanged -= OnPlaybackService_TrackChanged;
        _playbackService.PositionChanged -= OnPlaybackService_PositionChanged;
        _playbackService.DurationChanged -= OnPlaybackService_DurationChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackService_PlaybackStateChanged;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}