using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Nagi.WinUI.Models;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

public partial class LyricsPageViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan OptimisticUpdateGracePeriod = TimeSpan.FromSeconds(2);
    private readonly IDispatcherService _dispatcherService;
    private readonly ILibraryReader _libraryReader;
    private readonly ILyricRomanizationService _lyricRomanizationService;
    private readonly ILogger<LyricsPageViewModel> _logger;
    private readonly ILrcService _lrcService;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IRomanizationPackManager _romanizationPackManager;
    private readonly ISettingsService _settingsService;
    private readonly IUIService _uiService;
    private static readonly TimeSpan _seekTimeOffset = TimeSpan.FromSeconds(0.2);
    private readonly object _lyricsFetchLock = new();

    private CancellationTokenSource? _lyricsFetchCts;
    private bool _isDisposed;
    private int _lrcSearchHint;

    private LyricLine? _optimisticallySetLine;
    private long _optimisticSetTimestamp;
    private ParsedLrc? _parsedLrc;
    private Song? _currentSong;

    internal Task PendingPrefetch { get; private set; } = Task.CompletedTask;

    public LyricsPageViewModel(
        IMusicPlaybackService playbackService,
        IDispatcherService dispatcherService,
        ILrcService lrcService,
        ILibraryReader libraryReader,
        ILyricRomanizationService lyricRomanizationService,
        ISettingsService settingsService,
        IRomanizationPackManager romanizationPackManager,
        IUIService uiService,
        ILogger<LyricsPageViewModel> logger)
    {
        _playbackService = playbackService;
        _dispatcherService = dispatcherService;
        _lrcService = lrcService;
        _libraryReader = libraryReader;
        _lyricRomanizationService = lyricRomanizationService;
        _settingsService = settingsService;
        _romanizationPackManager = romanizationPackManager;
        _uiService = uiService;
        _logger = logger;

        _playbackService.TrackChanged += OnPlaybackServiceTrackChanged;
        _playbackService.PositionChanged += OnPlaybackServicePositionChanged;
        _playbackService.DurationChanged += OnPlaybackServiceDurationChanged;
        _playbackService.PlaybackStateChanged += OnPlaybackServicePlaybackStateChanged;
        _settingsService.LyricsRomanizationEnabledChanged += OnLyricsRomanizationEnabledChanged;
        _romanizationPackManager.PacksChanged += OnRomanizationPacksChanged;

        _ = UpdateForTrack(_playbackService.CurrentTrack);
        IsPlaying = _playbackService.IsPlaying;
    }

    [ObservableProperty] public partial string SongTitle { get; set; } = Nagi.WinUI.Resources.Strings.Lyrics_NoSongSelected;

    /// <summary>
    ///     True when synchronized (timed) lyrics are available and being displayed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    public partial bool HasLyrics { get; set; }

    /// <summary>
    ///     True when only unsynchronized (plain text) lyrics are available.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    public partial bool HasUnsyncedLyrics { get; set; }

    [ObservableProperty] public partial LyricLine? CurrentLine { get; set; }

    [ObservableProperty] public partial TimeSpan SongDuration { get; set; }

    [ObservableProperty] public partial TimeSpan CurrentPosition { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial bool CanEditLyrics { get; set; }
    [ObservableProperty] public partial bool CanRemoveLyrics { get; set; }
    [ObservableProperty] public partial string EditableLyrics { get; set; } = string.Empty;
    public Guid? CurrentSongId => _currentSong?.Id;

    public bool ShowNoLyricsMessage => !HasLyrics && !HasUnsyncedLyrics && !IsLoading;

    public ObservableRangeCollection<LyricLine> LyricLines { get; } = new();

    public ObservableRangeCollection<LyricLine> UnsyncedLyricLines { get; } = new();

    public void Dispose()
    {
        lock (_lyricsFetchLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _lyricsFetchCts?.Cancel();
            _lyricsFetchCts?.Dispose();
            _lyricsFetchCts = null;
        }

        _playbackService.TrackChanged -= OnPlaybackServiceTrackChanged;
        _playbackService.PositionChanged -= OnPlaybackServicePositionChanged;
        _playbackService.DurationChanged -= OnPlaybackServiceDurationChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackServicePlaybackStateChanged;
        _settingsService.LyricsRomanizationEnabledChanged -= OnLyricsRomanizationEnabledChanged;
        _romanizationPackManager.PacksChanged -= OnRomanizationPacksChanged;

    }

    [RelayCommand]
    public async Task SeekToLineAsync(LyricLine? line)
    {
        if (_isDisposed || line is null || !LyricLines.Contains(line)) return;

        var targetTime = line.StartTime - _seekTimeOffset;
        if (targetTime < TimeSpan.Zero) targetTime = TimeSpan.Zero;

        _logger.LogDebug("Seeking lyrics to line with start time {StartTime}", line.StartTime);
        _optimisticallySetLine = line;
        _optimisticSetTimestamp = Stopwatch.GetTimestamp();
        UpdateCurrentLineFromPosition(targetTime);

        await _playbackService.SeekAsync(targetTime);
    }

    /// <summary>
    ///     Opens the lyrics editor for the current song and saves or removes its lyrics as chosen.
    /// </summary>
    [RelayCommand]
    private async Task EditLyricsAsync()
    {
        if (CurrentSongId is not { } songId) return;

        var edit = await _uiService.ShowLyricsEditorAsync(EditableLyrics, CanRemoveLyrics);
        if (edit is null) return;

        try
        {
            if (edit.Action == LyricsEditAction.Save)
                await SaveLyricsAsync(songId, edit.Lyrics.ReplaceLineEndings());
            else
                await RemoveLyricsAsync(songId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update lyrics.");
            await _uiService.ShowMessageDialogAsync(Nagi.WinUI.Resources.Strings.LyricsPage_EditLyrics_Error_Title,
                Nagi.WinUI.Resources.Strings.LyricsPage_EditLyrics_Error_Message);
        }
    }

    private async Task SaveLyricsAsync(Guid songId, string lyrics)
    {
        var song = _currentSong?.Id == songId
            ? _currentSong
            : await _libraryReader.GetSongByIdAsync(songId).ConfigureAwait(false);
        if (song is null || string.IsNullOrWhiteSpace(lyrics)) return;

        await _lrcService.SaveLyricsAsync(song, lyrics).ConfigureAwait(false);
        if (_currentSong?.Id == song.Id) await UpdateForTrack(song).ConfigureAwait(false);
    }

    private async Task RemoveLyricsAsync(Guid songId)
    {
        var song = _currentSong?.Id == songId
            ? _currentSong
            : await _libraryReader.GetSongByIdAsync(songId).ConfigureAwait(false);
        if (song is null || !await _lrcService.RemoveCachedLyricsAsync(song).ConfigureAwait(false)) return;

        if (_currentSong?.Id == song.Id) await UpdateForTrack(song).ConfigureAwait(false);
    }

    private void OnPlaybackServicePositionChanged()
    {
        UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
    }

    private void UpdateCurrentLineFromPosition(TimeSpan position)
    {
        var parsedLrc = _parsedLrc;
        if (parsedLrc is null || !HasLyrics) return;

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed || !ReferenceEquals(parsedLrc, _parsedLrc)) return;
            CurrentPosition = position;
            var newCurrentLine = _lrcService.GetCurrentLine(parsedLrc, position, ref _lrcSearchHint);

            if (_optimisticallySetLine != null)
            {
                if (Stopwatch.GetElapsedTime(_optimisticSetTimestamp) < OptimisticUpdateGracePeriod)
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

            CurrentLine = newCurrentLine;
        });
    }

    private async Task UpdateForTrack(Song? song)
    {
        CancellationToken cancellationToken;
        lock (_lyricsFetchLock)
        {
            if (_isDisposed) return;
            _currentSong = song;
            _lyricsFetchCts?.Cancel();
            _lyricsFetchCts?.Dispose();
            _lyricsFetchCts = new CancellationTokenSource();
            cancellationToken = _lyricsFetchCts.Token;
        }

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed || cancellationToken.IsCancellationRequested) return;
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
            CanEditLyrics = false;
            CanRemoveLyrics = song is not null && _lrcService.HasCachedLyrics(song);
            EditableLyrics = string.Empty;

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

        try
        {
            var fullSongTask = _libraryReader.GetSongByIdAsync(song.Id);
            var localLrcTask = !string.IsNullOrWhiteSpace(song.LrcFilePath)
                ? _lrcService.GetLyricsAsync(song.LrcFilePath)
                : Task.FromResult<ParsedLrc?>(null);
            await Task.WhenAll(fullSongTask, localLrcTask).WaitAsync(cancellationToken).ConfigureAwait(false);

            var fullSong = await fullSongTask ?? song;
            var parsedLrc = await localLrcTask;
            var rawLyrics = parsedLrc?.RawLyrics ?? string.Empty;

            // Prefer sidecars, then embedded tags, and only then the network/cache.
            if (parsedLrc is not { IsEmpty: false } && string.IsNullOrWhiteSpace(rawLyrics))
            {
                if (!string.IsNullOrWhiteSpace(fullSong.Lyrics))
                {
                    rawLyrics = fullSong.Lyrics;
                    parsedLrc = _lrcService.ParseLyrics(rawLyrics);
                }
                else
                {
                    parsedLrc = await _lrcService.GetLyricsAsync(song, cancellationToken).ConfigureAwait(false);
                    rawLyrics = parsedLrc?.RawLyrics ?? string.Empty;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            var hasTimedLyrics = parsedLrc is { IsEmpty: false };
            IReadOnlyList<LyricLine> displayLines = [];
            if (hasTimedLyrics)
                displayLines = await _lyricRomanizationService.ApplyRomanizationAsync(parsedLrc!.Lines, cancellationToken).ConfigureAwait(false);
            else if (!string.IsNullOrWhiteSpace(rawLyrics))
                displayLines = await _lyricRomanizationService.ApplyRomanizationAsync(
                    ParseUnsyncedLyricsToLines(rawLyrics), cancellationToken).ConfigureAwait(false);

            _dispatcherService.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested || _isDisposed) return;
                EditableLyrics = rawLyrics;
                CanEditLyrics = true;
                CanRemoveLyrics = _lrcService.HasCachedLyrics(song);
                IsLoading = false;
                if (hasTimedLyrics)
                {
                    _parsedLrc = new ParsedLrc(displayLines, rawLyrics);
                    LyricLines.ReplaceRange(displayLines);
                    HasLyrics = true;
                    UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
                }
                else
                {
                    UnsyncedLyricLines.ReplaceRange(displayLines);
                    HasUnsyncedLyrics = displayLines.Count > 0;
                }
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Lyrics fetch for '{SongTitle}' was cancelled", song.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch lyrics for track '{SongTitle}'", song.Title);
            _dispatcherService.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested || _isDisposed) return;
                CanEditLyrics = true;
                IsLoading = false;
            });
        }

        if (!cancellationToken.IsCancellationRequested)
            PendingPrefetch = PrefetchNextTrackLyrics(cancellationToken);
    }

    /// <summary>
    ///     Parses unsynchronized lyrics text into individual lines for display,
    ///     normalizing line breaks and preserving verse structure with blank lines.
    /// </summary>
    private static string[] ParseUnsyncedLyricsToLines(string rawLyrics)
    {
        // Strip language prefixes like "eng||" or "jpn||" at the start of the text
        rawLyrics = LanguagePrefixRegex().Replace(rawLyrics, string.Empty);

        var normalized = rawLyrics.ReplaceLineEndings("\n");

        // Collapse multiple consecutive blank lines into double line breaks
        // This preserves verse separation while cleaning up excessive spacing
        normalized = MultipleNewlines().Replace(normalized, "\n\n");

        return normalized.Split('\n', StringSplitOptions.TrimEntries);
    }

    private void OnPlaybackServiceTrackChanged()
    {
        _ = UpdateForTrack(_playbackService.CurrentTrack);
    }

    private void OnLyricsRomanizationEnabledChanged(bool isEnabled)
    {
        _ = UpdateForTrack(_playbackService.CurrentTrack);
    }

    private void OnRomanizationPacksChanged()
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
    private async Task PrefetchNextTrackLyrics(CancellationToken cancellationToken)
    {
        try
        {
            var nextSong = await GetNextSongInQueueAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (nextSong is null || nextSong.LyricsLastCheckedUtc.HasValue ||
                !string.IsNullOrWhiteSpace(nextSong.LrcFilePath) || !string.IsNullOrWhiteSpace(nextSong.Lyrics)) return;

            using var prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            prefetchCts.CancelAfter(TimeSpan.FromSeconds(15));
            await _lrcService.GetLyricsAsync(nextSong, prefetchCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-fetch failed for next track");
        }
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleNewlines();

    [GeneratedRegex(@"^[a-z]{2,3}\|\|", RegexOptions.IgnoreCase)]
    private static partial Regex LanguagePrefixRegex();

    private Task<Song?> GetNextSongInQueueAsync()
    {
        var shuffled = _playbackService.IsShuffleEnabled;
        var queue = shuffled ? _playbackService.ShuffledQueue : _playbackService.PlaybackQueue;
        var nextIndex = (shuffled ? _playbackService.CurrentShuffledIndex : _playbackService.CurrentQueueIndex) + 1;
        return nextIndex >= 0 && nextIndex < queue.Count
            ? _libraryReader.GetSongByIdAsync(queue[nextIndex])
            : Task.FromResult<Song?>(null);
    }
}
