using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
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
    private readonly object _lyricsFetchLock = new();
    private CancellationTokenSource? _lyricsFetchCts;
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

        // Cancel any pending lyrics fetch operation
        lock (_lyricsFetchLock)
        {
            _lyricsFetchCts?.Cancel();
            _lyricsFetchCts?.Dispose();
            _lyricsFetchCts = null;
        }

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
            if (_isDisposed) return;
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
        // Cancel any previous lyrics fetch operation to prevent stale data when skipping songs
        CancellationToken cancellationToken;
        lock (_lyricsFetchLock)
        {
            _lyricsFetchCts?.Cancel();
            _lyricsFetchCts?.Dispose();
            _lyricsFetchCts = new CancellationTokenSource();
            cancellationToken = _lyricsFetchCts.Token;
        }

        // Reset UI state immediately on dispatcher
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
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
            }
            else
            {
                SongTitle = !string.IsNullOrWhiteSpace(song.Artist?.Name)
                    ? $"{song.Title} by {song.Artist.Name}"
                    : song.Title;
                SongDuration = _playbackService.Duration;
                IsLoading = true;
            }
        });

        if (song is null) return;

        _logger.LogDebug("Updating lyrics for track '{SongTitle}' ({SongId})", song.Title, song.Id);
        
        // Start prefetch for next track immediately (runs in parallel with current fetch)
        PrefetchNextTrackLyrics();

        // Perform I/O-bound operations on background thread to avoid blocking audio playback
        ParsedLrc? parsedLrc = null;
        List<string>? unsyncedLines = null;

        try
        {
            // 1. Start fetching both synchronized lyrics (often network/IO) and full song data (local fallback) in parallel.
            var lrcTask = _lrcService.GetLyricsAsync(song, cancellationToken);
            var songDataTask = _libraryReader.GetSongWithFullDataAsync(song.Id);

            try
            {
                await Task.WhenAll(lrcTask, songDataTask).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "One or more lyrics fetch tasks failed for '{SongTitle}', attempting to continue with available data.", song.Title);
            }

            parsedLrc = lrcTask.Status == TaskStatus.RanToCompletion ? lrcTask.Result : null;
            var fullSong = songDataTask.Status == TaskStatus.RanToCompletion ? songDataTask.Result : null;

            // Check if a newer track change occurred while we were fetching
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Lyrics fetch for '{SongTitle}' was cancelled (song changed)", song.Title);
                return;
            }

            if (parsedLrc is null || parsedLrc.IsEmpty)
            {
                if (fullSong is not null && !string.IsNullOrWhiteSpace(fullSong.Lyrics))
                {
                    _logger.LogDebug("Using unsynchronized lyrics fallback for track '{SongTitle}'", song.Title);
                    unsyncedLines = ParseUnsyncedLyricsToLines(fullSong.Lyrics);
                }
                else
                {
                    _logger.LogDebug("No lyrics found for track '{SongTitle}'", song.Title);
                }
            }
            else
            {
                _logger.LogDebug("Successfully parsed synchronized lyrics for track '{SongTitle}'", song.Title);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Lyrics fetch for '{SongTitle}' was cancelled", song.Title);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch lyrics for track '{SongTitle}'", song.Title);
        }

        // Final cancellation check before updating UI
        if (cancellationToken.IsCancellationRequested) return;

        // Update UI on dispatcher thread with fetched data
        _dispatcherService.TryEnqueue(() =>
        {
            // Double-check cancellation on UI thread in case a new fetch started
            if (cancellationToken.IsCancellationRequested || _isDisposed) return;

            IsLoading = false;

            if (parsedLrc is not null && !parsedLrc.IsEmpty)
            {
                _parsedLrc = parsedLrc;
                foreach (var line in parsedLrc.Lines) LyricLines.Add(line);
                HasLyrics = true;
                UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
                UpdateLineOpacities();
            }
            else if (unsyncedLines is not null)
            {
                foreach (var line in unsyncedLines) UnsyncedLyricLines.Add(line);
                HasUnsyncedLyrics = true;
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
        _dispatcherService.TryEnqueue(() => 
        { 
            if (_isDisposed) return;
            SongDuration = _playbackService.Duration; 
        });
    }


    private void OnPlaybackServicePlaybackStateChanged()
    {
        _dispatcherService.TryEnqueue(() => 
        { 
            if (_isDisposed) return;
            IsPlaying = _playbackService.IsPlaying; 
        });
    }
    
    /// <summary>
    ///     Pre-fetches lyrics for the next track in the queue to reduce perceived latency.
    ///     This is fire-and-forget - failures are silently logged and don't affect current playback.
    /// </summary>
    private async void PrefetchNextTrackLyrics()
    {
        CancellationTokenSource? prefetchCts = null;
        try
        {
            var nextSong = GetNextSongInQueue();
            if (nextSong is null) return;
            
            // Only prefetch if lyrics haven't been checked yet
            if (nextSong.LyricsLastCheckedUtc != null) return;
            
            _logger.LogDebug("Pre-fetching lyrics for next track: {Title}", nextSong.Title);
            
            // Link to main CTS for fast cancellation on track change/navigation, with 15s timeout as fallback
            lock (_lyricsFetchLock)
            {
                if (_lyricsFetchCts == null || _isDisposed) return;
                prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(_lyricsFetchCts.Token);
            }
            
            // Check if already cancelled immediately after creation (handles race where main CTS was cancelled during lock)
            if (prefetchCts.Token.IsCancellationRequested) return;
            prefetchCts.CancelAfter(TimeSpan.FromSeconds(15));
            
            await _lrcService.GetLyricsAsync(nextSong, prefetchCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the pre-fetch times out or is cancelled - no need to log
        }
        catch (Exception ex)
        {
            // Pre-fetch failures are non-critical
            _logger.LogDebug(ex, "Pre-fetch failed for next track (non-critical)");
        }
        finally
        {
            prefetchCts?.Dispose();
        }
    }
    
    private Core.Models.Song? GetNextSongInQueue()
    {
        if (_playbackService.IsShuffleEnabled)
        {
            var nextIndex = _playbackService.CurrentShuffledIndex + 1;
            return nextIndex < _playbackService.ShuffledQueue.Count 
                ? _playbackService.ShuffledQueue[nextIndex] 
                : null;
        }
        
        var nextQueueIndex = _playbackService.CurrentQueueIndex + 1;
        return nextQueueIndex < _playbackService.PlaybackQueue.Count 
            ? _playbackService.PlaybackQueue[nextQueueIndex] 
            : null;
    }
}
