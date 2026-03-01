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
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     Manages the state and logic for the LyricsPage. It interfaces with playback
///     services to get song information and lyrics, and provides properties for data
///     binding to the view.
/// </summary>
public partial class LyricsPageViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan OptimisticUpdateGracePeriod = TimeSpan.FromSeconds(2);
    private readonly IDispatcherService _dispatcherService;
    private readonly ILibraryReader _libraryReader;
    private readonly ILogger<LyricsPageViewModel> _logger;
    private readonly ILrcService _lrcService;
    private readonly IMusicPlaybackService _playbackService;
    private static readonly TimeSpan _seekTimeOffset = TimeSpan.FromSeconds(0.2);
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

        _ = UpdateForTrack(_playbackService.CurrentTrack);
        IsPlaying = _playbackService.IsPlaying;
    }

    [ObservableProperty] public partial string SongTitle { get; set; } = Nagi.WinUI.Resources.Strings.Lyrics_NoSongSelected;

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
    public bool ShowTimedLyrics => HasLyrics;

    /// <summary>
    ///     True when the "No lyrics found" message should be displayed.
    ///     This is only when we are not loading and have no lyrics of either type.
    /// </summary>
    public bool ShowNoLyricsMessage => !HasLyrics && !HasUnsyncedLyrics && !IsLoading;

    public ObservableRangeCollection<LyricLine> LyricLines { get; } = new();

    /// <summary>
    ///     Collection of unsynchronized lyric lines for display in a ListView-like format.
    /// </summary>
    public ObservableRangeCollection<string> UnsyncedLyricLines { get; } = new();

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
            if (_isDisposed || _parsedLrc is null) return;
            CurrentPosition = position;
            var newCurrentLine = _lrcService.GetCurrentLine(_parsedLrc, position, ref _lrcSearchHint);

            if (_optimisticallySetLine != null)
            {
                if (DateTime.UtcNow - _optimisticSetTimestamp < OptimisticUpdateGracePeriod)
                {
                    // If the "natural" current line is already the one we optimistically set, 
                    // we can clear the optimistic flag early as the seek has successfully caught up.
                    if (ReferenceEquals(newCurrentLine, _optimisticallySetLine))
                    {
                        _optimisticallySetLine = null;
                    }
                    else
                    {
                        newCurrentLine = _optimisticallySetLine;
                    }
                }
                else
                {
                    _optimisticallySetLine = null;
                }
            }

            if (!ReferenceEquals(newCurrentLine, CurrentLine))
            {
                CurrentLine = newCurrentLine;
            }
        });
    }

    private async Task UpdateForTrack(Song? song)
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
                SongTitle = Nagi.WinUI.Resources.Strings.Lyrics_NoSongSelected;
                IsLoading = false;
            }
            else
            {
                SongTitle = !string.IsNullOrWhiteSpace(song.ArtistName)
                    ? string.Format(Nagi.WinUI.Resources.Strings.Lyrics_SongTitleFormat, song.Title, song.ArtistName)
                    : song.Title;
                SongDuration = _playbackService.Duration;
                IsLoading = true;
            }
        });

        if (song is null) return;

        _logger.LogDebug("Updating lyrics for track '{SongTitle}' ({SongId})", song.Title, song.Id);
        
        // Start prefetch for next track immediately (runs in parallel with current fetch)
        _ = PrefetchNextTrackLyrics();

        try
        {
            // Fetch full song data in parallel with .lrc check since both are local/fast
            var fullSongTask = _libraryReader.GetSongWithFullDataAsync(song.Id);
            var localLrcTask = !string.IsNullOrWhiteSpace(song.LrcFilePath)
                ? _lrcService.GetLyricsAsync(song.LrcFilePath)
                : Task.FromResult<ParsedLrc?>(null);

            await Task.WhenAll(fullSongTask, localLrcTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;

            var fullSong = fullSongTask.Result;
            var localLrc = localLrcTask.Result;

            // Priority 1: Local .lrc sidecar (synced lyrics)
            if (localLrc is not null && !localLrc.IsEmpty)
            {
                _logger.LogDebug("Using local .lrc for track '{SongTitle}'", song.Title);
                _dispatcherService.TryEnqueue(() =>
                {
                    if (cancellationToken.IsCancellationRequested || _isDisposed) return;
                    _parsedLrc = localLrc;
                    LyricLines.AddRange(localLrc.Lines);
                    HasLyrics = true;
                    IsLoading = false;
                    UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
                });
                return;
            }

            // Priority 2: Embedded lyrics tag (skip network)
            if (fullSong is not null && !string.IsNullOrWhiteSpace(fullSong.Lyrics))
            {
                _logger.LogDebug("Using embedded lyrics for track '{SongTitle}'", song.Title);
                var parsedEmbedded = _lrcService.ParseLyrics(fullSong.Lyrics);
                
                if (parsedEmbedded is not null && !parsedEmbedded.IsEmpty)
                {
                    _dispatcherService.TryEnqueue(() =>
                    {
                        if (cancellationToken.IsCancellationRequested || _isDisposed) return;
                        _parsedLrc = parsedEmbedded;
                        LyricLines.AddRange(parsedEmbedded.Lines);
                        HasLyrics = true;
                        IsLoading = false;
                        UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
                    });
                    return;
                }
                var embeddedLines = ParseUnsyncedLyricsToLines(parsedEmbedded?.RawUnsyncedLyrics ?? fullSong.Lyrics);
                _dispatcherService.TryEnqueue(() =>
                {
                    if (cancellationToken.IsCancellationRequested || _isDisposed) return;
                    UnsyncedLyricLines.AddRange(embeddedLines);
                    HasUnsyncedLyrics = true;
                    IsLoading = false;
                });
                return;
            }

            // Priority 3: Network/cache (only when no local lyrics exist)
            var parsedLrc = await _lrcService.GetLyricsAsync(song, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;

            if (parsedLrc is not null && !parsedLrc.IsEmpty)
            {
                _logger.LogDebug("Fetched synced lyrics for track '{SongTitle}'", song.Title);
                _dispatcherService.TryEnqueue(() =>
                {
                    if (cancellationToken.IsCancellationRequested || _isDisposed) return;
                    _parsedLrc = parsedLrc;
                    LyricLines.AddRange(parsedLrc.Lines);
                    HasLyrics = true;
                    IsLoading = false;
                    UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
                });
            }
            else if (parsedLrc is not null && !string.IsNullOrWhiteSpace(parsedLrc.RawUnsyncedLyrics))
            {
                _logger.LogDebug("Fetched unsynced lyrics for track '{SongTitle}'", song.Title);
                var unsyncedLines = ParseUnsyncedLyricsToLines(parsedLrc.RawUnsyncedLyrics);
                _dispatcherService.TryEnqueue(() =>
                {
                    if (cancellationToken.IsCancellationRequested || _isDisposed) return;
                    UnsyncedLyricLines.AddRange(unsyncedLines);
                    HasUnsyncedLyrics = true;
                    IsLoading = false;
                });
            }
            else
            {
                _logger.LogDebug("No lyrics found for track '{SongTitle}'", song.Title);
                _dispatcherService.TryEnqueue(() =>
                {
                    if (cancellationToken.IsCancellationRequested || _isDisposed) return;
                    IsLoading = false;
                });
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
            _dispatcherService.TryEnqueue(() => IsLoading = false);
        }
    }

    /// <summary>
    ///     Parses unsynchronized lyrics text into individual lines for display,
    ///     normalizing line breaks and preserving verse structure with blank lines.
    /// </summary>
    private static List<string> ParseUnsyncedLyricsToLines(string rawLyrics)
    {
        // Strip language prefixes like "eng||" or "jpn||" at the start of the text
        rawLyrics = LanguagePrefixRegex().Replace(rawLyrics, string.Empty);

        // Normalize different line ending styles to \n
        var normalized = rawLyrics
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        // Collapse multiple consecutive blank lines into double line breaks
        // This preserves verse separation while cleaning up excessive spacing
        normalized = MultipleNewlines().Replace(normalized, "\n\n");

        // Split into lines and trim each line
        return normalized
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();
    }

    private void OnPlaybackServiceTrackChanged()
    {
        _ = UpdateForTrack(_playbackService.CurrentTrack);
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
    private async Task PrefetchNextTrackLyrics()
    {
        CancellationTokenSource? prefetchCts = null;
        try
        {
            var nextSong = await GetNextSongInQueueAsync().ConfigureAwait(false);
            if (nextSong is null) return;

            // Skip if already network-checked
            if (nextSong.LyricsLastCheckedUtc != null) return;

            // Skip if a local .lrc sidecar exists — it loads from disk when the track plays
            if (!string.IsNullOrWhiteSpace(nextSong.LrcFilePath)) return;

            // Skip if embedded lyrics exist — they load from the DB when the track plays
            var fullNextSong = await _libraryReader.GetSongWithFullDataAsync(nextSong.Id).ConfigureAwait(false);
            if (fullNextSong is not null && !string.IsNullOrWhiteSpace(fullNextSong.Lyrics)) return;

            // No local lyrics — worth prefetching from network.
            // Wait 1 second to reduce peak concurrent HTTP request count.
            CancellationToken earlyCancelToken;
            lock (_lyricsFetchLock)
            {
                if (_lyricsFetchCts == null || _isDisposed) return;
                earlyCancelToken = _lyricsFetchCts.Token;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), earlyCancelToken).ConfigureAwait(false);

            lock (_lyricsFetchLock)
            {
                if (_lyricsFetchCts == null || _isDisposed) return;
                prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(_lyricsFetchCts.Token);
            }

            if (prefetchCts.Token.IsCancellationRequested) return;
            prefetchCts.CancelAfter(TimeSpan.FromSeconds(15));

            await _lrcService.GetLyricsAsync(nextSong, prefetchCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the pre-fetch times out or is cancelled
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-fetch failed for next track (non-critical)");
        }
        finally
        {
            prefetchCts?.Dispose();
        }
    }
    
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleNewlines();

    [GeneratedRegex(@"^[a-z]{2,3}\|\|", RegexOptions.IgnoreCase)]
    private static partial Regex LanguagePrefixRegex();

    private async Task<Song?> GetNextSongInQueueAsync()
    {
        Guid nextId = Guid.Empty;
        if (_playbackService.IsShuffleEnabled)
        {
            var nextIndex = _playbackService.CurrentShuffledIndex + 1;
            if (nextIndex < _playbackService.ShuffledQueue.Count)
                nextId = _playbackService.ShuffledQueue[nextIndex];
        }
        else
        {
            var nextQueueIndex = _playbackService.CurrentQueueIndex + 1;
            if (nextQueueIndex < _playbackService.PlaybackQueue.Count)
                nextId = _playbackService.PlaybackQueue[nextQueueIndex];
        }

        if (nextId == Guid.Empty) return null;

        // Fetch basic metadata for the next song (single lookup is more efficient than dictionary)
        return await _libraryReader.GetSongByIdAsync(nextId).ConfigureAwait(false);
    }
}
