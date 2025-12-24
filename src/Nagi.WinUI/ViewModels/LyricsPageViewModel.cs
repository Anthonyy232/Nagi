using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Manages the state and logic for the LyricsPage. It interfaces with playback
///     services to get song information and lyrics, and provides properties for data
///     binding to the view.
/// </summary>
public partial class LyricsPageViewModel : ObservableObject, IDisposable
{
    /// <summary>
    ///     The base for the exponential fade. A value closer to 1.0 (e.g., 0.9) means a
    ///     very slow and gradual fade. A smaller value (e.g., 0.75) means a faster fade.
    /// </summary>
    private const double GradualFadeBase = 0.65;

    /// <summary>
    ///     The minimum opacity a line can have, ensuring it's never fully invisible.
    /// </summary>
    private const double MinimumOpacity = 0.10;

    private static readonly TimeSpan OptimisticUpdateGracePeriod = TimeSpan.FromSeconds(2);
    private readonly IDispatcherService _dispatcherService;
    private readonly ILibraryReader _libraryReader;
    private readonly ILogger<LyricsPageViewModel> _logger;
    private readonly ILrcService _lrcService;
    private readonly IMusicPlaybackService _playbackService;
    private readonly TimeSpan _seekTimeOffset = TimeSpan.FromSeconds(0.2);
    private bool _isDisposed;
    private int _lrcSearchHint;

    private LyricLine? _optimisticallySetLine;
    private DateTime _optimisticSetTimestamp;
    private ParsedLrc? _parsedLrc;

    public LyricsPageViewModel(
        IMusicPlaybackService playbackService,
        IDispatcherService dispatcherService,
        ILrcService lrcService,
        ILibraryReader libraryReader,
        ILogger<LyricsPageViewModel> logger)
    {
        _playbackService = playbackService;
        _dispatcherService = dispatcherService;
        _lrcService = lrcService;
        _libraryReader = libraryReader;
        _logger = logger;

        _playbackService.TrackChanged += OnPlaybackServiceTrackChanged;
        _playbackService.PositionChanged += OnPlaybackServicePositionChanged;
        _playbackService.DurationChanged += OnPlaybackServiceDurationChanged;
        _playbackService.PlaybackStateChanged += OnPlaybackServicePlaybackStateChanged;

        UpdateForTrack(_playbackService.CurrentTrack);
        IsPlaying = _playbackService.IsPlaying;
    }

    [ObservableProperty] public partial string SongTitle { get; set; } = "No song selected";

    /// <summary>
    ///     True when synchronized (timed) lyrics are available and being displayed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    [NotifyPropertyChangedFor(nameof(ShowTimedLyrics))]
    public partial bool HasLyrics { get; set; }

    /// <summary>
    ///     True when only unsynchronized (plain text) lyrics are available.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    [NotifyPropertyChangedFor(nameof(ShowTimedLyrics))]
    public partial bool HasUnsyncedLyrics { get; set; }

    [ObservableProperty] public partial LyricLine? CurrentLine { get; set; }

    [ObservableProperty] public partial TimeSpan SongDuration { get; set; }

    [ObservableProperty] public partial TimeSpan CurrentPosition { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    public partial bool IsPlaying { get; set; }
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    public partial bool IsLoading { get; set; }

    /// <summary>
    ///     True when the timed lyrics ListView should be displayed.
    /// </summary>
    public bool ShowTimedLyrics => HasLyrics && !HasUnsyncedLyrics;

    /// <summary>
    ///     True when the "No lyrics found" message should be displayed.
    ///     This is only when we are not loading and have no lyrics of either type.
    /// </summary>
    public bool ShowNoLyricsMessage => !HasLyrics && !HasUnsyncedLyrics && !IsLoading;

    public ObservableCollection<LyricLine> LyricLines { get; } = new();

    /// <summary>
    ///     Collection of unsynchronized lyric lines for display in a ListView-like format.
    /// </summary>
    public ObservableCollection<string> UnsyncedLyricLines { get; } = new();

    public void Dispose()
    {
        if (_isDisposed) return;

        _playbackService.TrackChanged -= OnPlaybackServiceTrackChanged;
        _playbackService.PositionChanged -= OnPlaybackServicePositionChanged;
        _playbackService.DurationChanged -= OnPlaybackServiceDurationChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackServicePlaybackStateChanged;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    public async Task SeekToLineAsync(LyricLine? line)
    {
        if (line is null) return;

        var targetTime = line.StartTime - _seekTimeOffset;
        if (targetTime < TimeSpan.Zero) targetTime = TimeSpan.Zero;

        _logger.LogDebug("Seeking lyrics to line with start time {StartTime}", line.StartTime);
        _optimisticallySetLine = line;
        _optimisticSetTimestamp = DateTime.UtcNow;
        UpdateCurrentLineFromPosition(targetTime);

        await _playbackService.SeekAsync(targetTime);
    }

    private void OnPlaybackServicePositionChanged()
    {
        UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
    }

    private void UpdateCurrentLineFromPosition(TimeSpan position)
    {
        if (_parsedLrc is null || !HasLyrics) return;

        _dispatcherService.TryEnqueue(() =>
        {
            CurrentPosition = position;
            var newCurrentLine = _lrcService.GetCurrentLine(_parsedLrc, position, ref _lrcSearchHint);

            if (_optimisticallySetLine != null &&
                DateTime.UtcNow - _optimisticSetTimestamp < OptimisticUpdateGracePeriod)
            {
                if (!ReferenceEquals(newCurrentLine, _optimisticallySetLine)) newCurrentLine = _optimisticallySetLine;
            }
            else
            {
                _optimisticallySetLine = null;
            }

            if (!ReferenceEquals(newCurrentLine, CurrentLine))
            {
                if (CurrentLine != null) CurrentLine.IsActive = false;
                if (newCurrentLine != null) newCurrentLine.IsActive = true;
                CurrentLine = newCurrentLine;
                UpdateLineOpacities();
            }
        });
    }

    /// <summary>
    ///     Calculates and applies opacity to each lyric line based on its distance
    ///     from the currently active line using a gradual, exponential curve.
    /// </summary>
    private void UpdateLineOpacities()
    {
        if (LyricLines.Count == 0) return;

        var activeIndex = CurrentLine != null ? LyricLines.IndexOf(CurrentLine) : -1;

        // If no line is active, set all lines to the minimum opacity.
        if (activeIndex == -1)
        {
            foreach (var line in LyricLines) line.Opacity = MinimumOpacity;
            return;
        }

        for (var i = 0; i < LyricLines.Count; i++)
        {
            var distance = Math.Abs(i - activeIndex);

            // Use Math.Pow for a smooth, exponential decay.
            var gradualOpacity = Math.Pow(GradualFadeBase, distance);

            // Ensure the opacity never drops below the defined minimum.
            LyricLines[i].Opacity = Math.Max(MinimumOpacity, gradualOpacity);
        }
    }

    private async void UpdateForTrack(Song? song)
    {
        await _dispatcherService.EnqueueAsync(async () =>
        {
            LyricLines.Clear();
            UnsyncedLyricLines.Clear();
            CurrentLine = null;
            _parsedLrc = null;
            _lrcSearchHint = 0;
            _optimisticallySetLine = null;
            SongDuration = TimeSpan.Zero;
            CurrentPosition = TimeSpan.Zero;
            HasLyrics = false;
            HasUnsyncedLyrics = false;

            if (song is null)
            {
                _logger.LogDebug("Clearing lyrics view as playback stopped");
                SongTitle = "No song selected";
                return;
            }

            _logger.LogDebug("Updating lyrics for track '{SongTitle}' ({SongId})", song.Title, song.Id);
            SongTitle = !string.IsNullOrWhiteSpace(song.Artist?.Name)
                ? $"{song.Title} by {song.Artist.Name}"
                : song.Title;
            SongDuration = _playbackService.Duration;

            IsLoading = true;
            try
            {
                // 1. Try to get synchronized (timed) lyrics first
                _parsedLrc = await _lrcService.GetLyricsAsync(song);

                if (_parsedLrc is not null && !_parsedLrc.IsEmpty)
                {
                    _logger.LogDebug("Successfully parsed synchronized lyrics for track '{SongTitle}'", song.Title);
                    foreach (var line in _parsedLrc.Lines) LyricLines.Add(line);
                    HasLyrics = true;
                    UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
                    UpdateLineOpacities();
                    return;
                }

                // 2. No sync lyrics found - try unsynchronized lyrics fallback
                // Need to fetch full song data since Lyrics is excluded from queue projections
                var fullSong = await _libraryReader.GetSongWithFullDataAsync(song.Id);
                if (fullSong is not null && !string.IsNullOrWhiteSpace(fullSong.Lyrics))
                {
                    _logger.LogDebug("Using unsynchronized lyrics fallback for track '{SongTitle}'", song.Title);
                    var lines = ParseUnsyncedLyricsToLines(fullSong.Lyrics);
                    foreach (var line in lines) UnsyncedLyricLines.Add(line);
                    HasUnsyncedLyrics = true;
                    return;
                }

                _logger.LogDebug("No lyrics found for track '{SongTitle}'", song.Title);
            }
            finally
            {
                IsLoading = false;
            }
        });
    }

    /// <summary>
    ///     Parses unsynchronized lyrics text into individual lines for display,
    ///     normalizing line breaks and preserving verse structure with blank lines.
    /// </summary>
    private static List<string> ParseUnsyncedLyricsToLines(string rawLyrics)
    {
        // Normalize different line ending styles to \n
        var normalized = rawLyrics
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        // Collapse multiple consecutive blank lines into double line breaks
        // This preserves verse separation while cleaning up excessive spacing
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");

        // Split into lines and trim each line
        return normalized
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();
    }

    private void OnPlaybackServiceTrackChanged()
    {
        UpdateForTrack(_playbackService.CurrentTrack);
    }

    private void OnPlaybackServiceDurationChanged()
    {
        _dispatcherService.TryEnqueue(() => { SongDuration = _playbackService.Duration; });
    }


    private void OnPlaybackServicePlaybackStateChanged()
    {
        _dispatcherService.TryEnqueue(() => { IsPlaying = _playbackService.IsPlaying; });
    }
}