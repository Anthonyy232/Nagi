using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models.Lyrics;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Core.Settings;

namespace Nagi.Presentation.ViewModels;

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
    private IReadOnlyList<LyricLine>? _syncedLines;
    private Song? _currentSong;
    private bool _canRemoveLyrics;

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
        _settingsService.SettingChanged += OnSettingChanged;
        _romanizationPackManager.PacksChanged += OnRomanizationPacksChanged;

        _ = UpdateForTrack(_playbackService.CurrentTrack);
        IsPlaying = _playbackService.IsPlaying;
    }

    [ObservableProperty] public partial string SongTitle { get; set; } = Strings.Lyrics_NoSongSelected;

    /// <summary>Synchronized lyrics are shown; plain text sets <see cref="HasUnsyncedLyrics" /> instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    public partial bool HasLyrics { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    public partial bool HasUnsyncedLyrics { get; set; }

    [ObservableProperty] public partial LyricLine? CurrentLine { get; set; }

    /// <summary>
    ///     The position of <see cref="CurrentLine" /> in <see cref="LyricLines" />, or -1 when there is none. It is
    ///     up to date when <see cref="CurrentLine" /> raises PropertyChanged.
    /// </summary>
    public int CurrentLineIndex { get; private set; } = -1;

    /// <summary>
    ///     Raised when newly loaded synchronized lyrics replace <see cref="LyricLines" />, before their first current
    ///     line is set, so a view can jump to that line instead of animating to it.
    /// </summary>
    public event Action? LyricsReplaced;

    [ObservableProperty] public partial TimeSpan SongDuration { get; set; }

    [ObservableProperty] public partial TimeSpan CurrentPosition { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLyricsMessage))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial bool CanEditLyrics { get; set; }
    [ObservableProperty] public partial string EditableLyrics { get; set; } = string.Empty;

    public bool ShowNoLyricsMessage => !HasLyrics && !HasUnsyncedLyrics && !IsLoading;

    public ObservableRangeCollection<LyricLine> LyricLines { get; } = new();

    public ObservableRangeCollection<LyricLine> UnsyncedLyricLines { get; } = new();

    /// <summary>
    ///     How far playback is through <see cref="CurrentLine" />, from 0 to 1, and the time until the next line starts.
    ///     The last line runs to the end of the song.
    /// </summary>
    public (double Fraction, TimeSpan Remaining) GetCurrentLineProgress()
    {
        if (CurrentLine is not { } line) return (0, TimeSpan.Zero);

        var index = CurrentLineIndex;
        var nextLineStart = index >= 0 && index < LyricLines.Count - 1 ? LyricLines[index + 1].StartTime : SongDuration;
        var lineDuration = nextLineStart - line.StartTime;
        if (lineDuration <= TimeSpan.Zero) return (1, TimeSpan.Zero);

        var positionInLine = CurrentPosition < line.StartTime ? TimeSpan.Zero : CurrentPosition - line.StartTime;
        return (Math.Clamp(positionInLine / lineDuration, 0, 1), lineDuration - positionInLine);
    }

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
        _settingsService.SettingChanged -= OnSettingChanged;
        _romanizationPackManager.PacksChanged -= OnRomanizationPacksChanged;

    }

    public async Task SeekToLineAsync(LyricLine? line)
    {
        if (_isDisposed || line is null || !LyricLines.Contains(line)) return;

        var targetTime = line.StartTime - _seekTimeOffset;
        if (targetTime < TimeSpan.Zero) targetTime = TimeSpan.Zero;

        _logger.LogDebug("Seeking lyrics to line with start time {StartTime}", line.StartTime);
        _optimisticallySetLine = line;
        _optimisticSetTimestamp = Stopwatch.GetTimestamp();
        UpdateCurrentLineFromPosition(targetTime);

        try { await _playbackService.SeekAsync(targetTime); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to seek to lyric line"); }
    }

    /// <summary>
    ///     Opens the lyrics editor for the current song and saves or removes its lyrics as chosen.
    /// </summary>
    [RelayCommand]
    private async Task EditLyricsAsync()
    {
        if (_currentSong?.Id is not { } songId) return;

        var edit = await _uiService.ShowLyricsEditorAsync(EditableLyrics, _canRemoveLyrics);
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
            await _uiService.ShowMessageDialogAsync(Strings.LyricsPage_EditLyrics_Error_Title,
                Strings.LyricsPage_EditLyrics_Error_Message);
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

    partial void OnCurrentLineChanged(LyricLine? value) =>
        CurrentLineIndex = value is null ? -1 : LyricLines.IndexOf(value);

    private void OnPlaybackServicePositionChanged()
    {
        UpdateCurrentLineFromPosition(_playbackService.CurrentPosition);
    }

    private void UpdateCurrentLineFromPosition(TimeSpan position)
    {
        var syncedLines = _syncedLines;
        if (syncedLines is null || !HasLyrics) return;

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed || !ReferenceEquals(syncedLines, _syncedLines)) return;
            CurrentPosition = position;
            var newCurrentLine = LyricsParser.GetCurrentLine(syncedLines, position, ref _lrcSearchHint);

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
            _syncedLines = null;
            _lrcSearchHint = 0;
            _optimisticallySetLine = null;
            SongDuration = TimeSpan.Zero;
            CurrentPosition = TimeSpan.Zero;
            HasLyrics = false;
            HasUnsyncedLyrics = false;
            CanEditLyrics = false;
            _canRemoveLyrics = song is not null && _lrcService.HasCachedLyrics(song);
            EditableLyrics = string.Empty;

            if (song is null)
            {
                _logger.LogDebug("Clearing lyrics view as playback stopped");
                SongTitle = Strings.Lyrics_NoSongSelected;
                IsLoading = false;
            }
            else
            {
                SongTitle = !string.IsNullOrWhiteSpace(song.ArtistName)
                    ? ResourceFormatter.Format(Strings.Lyrics_SongTitleFormat, song.Title, song.ArtistName)
                    : song.Title;
                SongDuration = _playbackService.Duration;
                IsLoading = true;
            }
        });

        if (song is null) return;

        _logger.LogDebug("Updating lyrics for track '{SongTitle}' ({SongId})", song.Title, song.Id);

        try
        {
            var fullSong = await _libraryReader.GetSongByIdAsync(song.Id).WaitAsync(cancellationToken).ConfigureAwait(false) ?? song;
            lock (_lyricsFetchLock)
            {
                // The lookup records where it cached the lyrics on the song it is given, which edits and removals read.
                if (ReferenceEquals(_currentSong, song)) _currentSong = fullSong;
            }

            var lyrics = await _lrcService.GetLyricsAsync(fullSong, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var displayLines = lyrics is null
                ? []
                : await _lyricRomanizationService.ApplyRomanizationAsync(lyrics.Lines, cancellationToken).ConfigureAwait(false);

            _dispatcherService.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested || _isDisposed) return;
                EditableLyrics = lyrics?.RawLyrics ?? string.Empty;
                CanEditLyrics = true;
                _canRemoveLyrics = _lrcService.HasCachedLyrics(fullSong);
                IsLoading = false;
                if (lyrics is { IsSynced: true })
                {
                    _syncedLines = displayLines;
                    LyricLines.ReplaceRange(displayLines);
                    HasLyrics = true;
                    LyricsReplaced?.Invoke();
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

    private void OnPlaybackServiceTrackChanged()
    {
        _ = UpdateForTrack(_playbackService.CurrentTrack);
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting != CoreSettings.LyricsRomanization) return;
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

    // Looks the next track's lyrics up ahead of time; the lyrics service decides whether that needs the network.
    private async Task PrefetchNextTrackLyrics(CancellationToken cancellationToken)
    {
        try
        {
            var nextSong = await GetNextSongInQueueAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (nextSong is null) return;

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

    // A queued file from outside the library has no library entry, but the queue knows it in full.
    private async Task<Song?> GetNextSongInQueueAsync()
    {
        if (_playbackService.NextTrackId is not { } id) return null;
        return await _libraryReader.GetSongByIdAsync(id).ConfigureAwait(false)
               ?? (await _playbackService.GetQueueSongsAsync([id]).ConfigureAwait(false)).GetValueOrDefault(id);
    }
}
