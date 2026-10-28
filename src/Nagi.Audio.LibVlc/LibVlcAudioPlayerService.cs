using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibVLCSharp;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Resources;

namespace Nagi.Audio.LibVlc;

/// <summary>
///     Implements <see cref="IAudioPlayer" /> using LibVLCSharp, and reports what is playing to the
///     <see cref="ISystemMediaControls" />.
///     Uses lazy initialization to avoid blocking app startup.
/// </summary>
public sealed partial class LibVlcAudioPlayerService : IAudioPlayer, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan NativeStopTimeout = TimeSpan.FromSeconds(5);

    private readonly IAppInfoService _appInfoService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ISystemMediaControls _mediaControls;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<LibVlcAudioPlayerService> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _vlcOperationLock = new(1, 1);
    private readonly ReaderWriterLockSlim _nativeLifetimeLock = new();
    private CancellationTokenSource? _loadCts;

    private readonly Lazy<Task> _disposeTask;
    private volatile bool _isInitialized;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Equalizer? _equalizer;

    private Media? _currentMedia;
    private Song? _currentSong;

    private volatile bool _isDisposed;
    private volatile bool _isExplicitStop; // Prevents false PlaybackEnded events on user/error stop
    private double _replayGainOffset;

    // Nagi intentionally keeps approximately 2 dB of headroom below LibVLC's
    // nominal 12 dB flat/unity preset.
    private float _basePreamp = EqualizerSettings.DefaultPreampDb;

    // Fade support: tracks user-set volume separately from the live VLC volume so that
    // fade animations do not propagate to the UI or persist to settings.
    private double _userVolume = 1.0;
    private volatile bool _isFading;
    private volatile bool _isFadeOnPlayPauseEnabled;
    private int _fadeInDurationMs = 200;
    private int _fadeOutDurationMs = 150;
    private CancellationTokenSource? _fadeCts;

    private float _playbackRate = 1f;

    // Tracks if we are currently fading out to pause, so IsPlaying can return false early
    private volatile bool _isPausing;

    public LibVlcAudioPlayerService(
        IDispatcherService dispatcherService,
        ISystemMediaControls mediaControls,
        IAppInfoService appInfoService,
        ISettingsService settingsService,
        ILogger<LibVlcAudioPlayerService> logger)
    {
        _dispatcherService = dispatcherService;
        _mediaControls = mediaControls;
        _appInfoService = appInfoService;
        _settingsService = settingsService;
        _logger = logger;
        _disposeTask = new Lazy<Task>(DisposeCoreAsync);
        _mediaControls.PlayPressed += OnMediaControlsPlayPressed;
        _mediaControls.PausePressed += OnMediaControlsPausePressed;
    }

    private void EnsureInitialized()
    {
        if (_isInitialized || _isDisposed) return;
        EnsureInitializedAsync().GetAwaiter().GetResult();
    }

    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized || _isDisposed) return;
        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isInitialized || _isDisposed) return;
            var useWasapiExclusive = await _settingsService.GetWasapiExclusiveModeEnabledAsync().ConfigureAwait(false);
            var outputDeviceId = await _settingsService.GetAudioOutputDeviceIdAsync().ConfigureAwait(false);
            await Task.Run(() => InitializeLibVlcCore(useWasapiExclusive, outputDeviceId), _disposeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_isDisposed)
        {
        }
        catch
        {
            DisposeNativeComponents();
            throw;
        }
        finally
        {
            _vlcOperationLock.Release();
        }
    }

    private void InitializeLibVlcCore(bool useWasapiExclusive, string outputDeviceId)
    {
        _logger.LogDebug("Initializing LibVLC core (deferred).");

        // Register the native library resolver in the same serialized path that creates
        // LibVLC. Callers can reach this method before the background audio warm-up runs
        // (for example, while DI constructs MusicPlaybackService for the taskbar service),
        // so initialization cannot safely live in App.InitializeCoreServicesAsync.
        LibVLCSharp.Core.Initialize();

        // LibVLC explicitly does not guarantee compatibility for constructor options.
        // Keep only the two behaviors that currently lack a suitable public LibVLC API.
        var vlcOptions = new List<string> { "--no-video", "--no-volume-save" };
        if (useWasapiExclusive && OperatingSystem.IsWindows()) vlcOptions.Add("--wasapi-exclusive");

        _logger.LogDebug("Initializing LibVLC with options: {VlcOptions}", string.Join(" ", vlcOptions));
        _libVlc = new LibVLC(false, vlcOptions.ToArray());

        // Subscribe before MediaPlayer construction so native audio-output diagnostics are captured.
        _libVlc.Log += OnLibVlcLog;

        var httpUserAgent = GetHttpUserAgent();
        _libVlc.SetUserAgent("Nagi", httpUserAgent);
        _logger.LogInformation(
            "LibVLC runtime: Version={Version}; Changeset={Changeset}; ABI={Abi}; Compiler={Compiler}; UserAgent={UserAgent}",
            _libVlc.Version,
            _libVlc.Changeset,
            _libVlc.ABI,
            _libVlc.LibVLCCompiler,
            httpUserAgent);

        _mediaPlayer = new MediaPlayer(_libVlc);
        ApplySavedAudioOutput(outputDeviceId);

        _equalizer = new Equalizer();
        _equalizer.SetPreamp(_basePreamp);
        _mediaPlayer.SetEqualizer(_equalizer);

        _mediaPlayer.PositionChanged += OnMediaPlayerPositionChanged;
        _mediaPlayer.Playing += OnMediaPlayerStateChanged;
        _mediaPlayer.Paused += OnMediaPlayerStateChanged;
        _mediaPlayer.Stopped += OnMediaPlayerStateChanged;
        _mediaPlayer.EncounteredError += OnMediaPlayerEncounteredError;
        _mediaPlayer.MediaChanged += OnMediaPlayerMediaChanged;
        _mediaPlayer.LengthChanged += OnMediaPlayerLengthChanged;
        _mediaPlayer.VolumeChanged += OnMediaPlayerVolumeChanged;
        _mediaPlayer.Muted += OnMediaPlayerMuteChanged;
        _mediaPlayer.Unmuted += OnMediaPlayerMuteChanged;

        _isInitialized = true;
        _logger.LogDebug("LibVLC initialization complete.");
    }

    private string GetHttpUserAgent()
    {
        try
        {
            var version = _appInfoService.GetAppVersion();
            if (!string.IsNullOrWhiteSpace(version)) return $"Nagi/{version.Trim()}";
        }
        catch (Exception ex)
        {
            // Package identity can be unavailable in some development-host scenarios.
            // User-agent metadata is useful but must never make playback initialization fatal.
            _logger.LogWarning(ex, "Could not determine the package version for LibVLC's HTTP user agent.");
        }

        return "Nagi";
    }

    private static bool UsesNativeDemuxer(string extension)
    {
        return extension is ".opus" or ".ogg" or ".oga" or ".webm";
    }

    private static string? GetAvFormatHint(string extension)
    {
        return extension switch
        {
            ".mp3" => "mp3",
            ".flac" => "flac",
            ".wav" => "wav",
            ".aac" => "aac",
            ".m4a" or ".m4b" or ".mp4" or ".m4v" => "mp4",
            ".wma" or ".asf" => "asf",
            ".aiff" => "aiff",
            ".ape" => "ape",
            ".dsf" => "dsf",
            // Musepack SV7 and SV8 use separate FFmpeg demuxers. Content probing is
            // required so an SV8 file is not forced through the SV7-only "mpc" demuxer.
            ".mpc" or ".mpp" => null,
            ".wv" => "wv",
            ".mpeg" or ".mpg" or ".mpe" => "mpeg",
            _ => null
        };
    }

    public event Action? PlaybackEnded, PositionChanged, StateChanged, VolumeChanged, MediaOpened, DurationChanged;
    public event Action<string>? ErrorOccurred;

    public bool IsPlaying => !_isPausing && ReadNative(() => _mediaPlayer?.State == VLCState.Playing, false);
    public TimeSpan CurrentPosition => ReadNative(() => FromVlcMicroseconds(_mediaPlayer?.Time ?? 0), TimeSpan.Zero);
    public TimeSpan Duration => ReadNative(() => FromVlcMicroseconds(_mediaPlayer?.Length ?? 0), TimeSpan.Zero);
    public double Volume => _isDisposed || !_isInitialized ? 0 : _userVolume;
    public bool IsMuted => ReadNative(() => _mediaPlayer?.Mute ?? false, false);

    public string AudioOutputDeviceId => ReadNative(() => _mediaPlayer?.OutputDevice ?? string.Empty, string.Empty);

    private T ReadNative<T>(Func<T> read, T unavailable)
    {
        // Native callbacks must not wait behind disposal, which can itself wait for callbacks.
        if (_isDisposed || !_isInitialized || !_nativeLifetimeLock.TryEnterReadLock(0)) return unavailable;
        try
        {
            return _isDisposed ? unavailable : read();
        }
        finally
        {
            _nativeLifetimeLock.ExitReadLock();
        }
    }

    public async Task LoadAsync(Song song)
    {
        if (_isDisposed) return;
        await EnsureInitializedAsync().ConfigureAwait(false);
        if (_isDisposed || _mediaPlayer is null) return;

        CancelFade();

        // Cancel any previous in-flight load immediately (cancel-and-supersede pattern).
        // When user rapidly skips tracks, only the final destination's VLC work executes.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var token = cts.Token;
        var previousLoad = Interlocked.Exchange(ref _loadCts, cts);
        previousLoad?.Cancel();
        previousLoad?.Dispose();

        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // If a newer LoadAsync call superseded us while we waited for the lock, skip entirely.
            token.ThrowIfCancellationRequested();
            _currentSong = song;

            _logger.LogDebug("Loading media for song '{SongTitle}' from path: {FilePath}", song.Title,
                song.FilePath);

            // Offload to thread pool to prevent blocking the UI thread.
            // VLC's Media constructor and Media setter internally parse file headers
            // and initialize demuxers, which can block for 2-3 seconds on large FLAC files.
            await Task.Run(() =>
            {
                if (_isDisposed) return;

                DisposeCurrentMedia("replacement load");

                var extension = System.IO.Path.GetExtension(song.FilePath).ToLowerInvariant();
                var useNativeDemuxer = UsesNativeDemuxer(extension);

                _currentMedia = new Media(song.FilePath, FromType.FromPath);

                // Native Ogg and Matroska demuxers integrate more safely with VLC's player clock.
                // FFmpeg remains preferred for the other formats because it handles edge-case MP3s
                // better than VLC's native elementary-stream demuxer.
                if (!useNativeDemuxer)
                {
                    _currentMedia.AddOption(":demux=avcodec");

                    var formatHint = GetAvFormatHint(extension);

                    if (formatHint != null)
                    {
                        _currentMedia.AddOption($":avformat-format={formatHint}");
                    }
                }

                _mediaPlayer!.Media = _currentMedia;
                // Do NOT dispose the media immediately - let it be disposed when no longer needed
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer LoadAsync call — this is expected during rapid skipping.
            _logger.LogDebug("LoadAsync for '{SongTitle}' was superseded by a newer load request.", song.Title);
        }
        catch (Exception ex)
        {
            var errorMessage = string.Format(Strings.Player_Error_LoadFailed, song.Title);
            _logger.LogError(ex, "Failed to load song '{SongTitle}' from path: {FilePath}", song.Title, song.FilePath);
            _dispatcherService.TryEnqueue(() => ErrorOccurred?.Invoke(errorMessage));
            _currentSong = null;

            try
            {
                if (_mediaPlayer is not null) _mediaPlayer.Media = null;
            }
            catch (Exception detachEx)
            {
                _logger.LogWarning(detachEx, "Failed to detach media after a load failure.");
            }

            DisposeCurrentMedia("load failure");
        }
        finally
        {
            _vlcOperationLock.Release();
        }
    }

    public async Task PlayAsync()
    {
        if (_isDisposed) return;
        await EnsureInitializedAsync().ConfigureAwait(false);
        if (_isDisposed || _mediaPlayer is null) return;

        // Cancel any ongoing fade (e.g. a fade-out from a previous pause that didn't complete)
        var newPlayCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var fadeCt = newPlayCts.Token;
        var oldPlayCts = Interlocked.Exchange(ref _fadeCts, newPlayCts);
        oldPlayCts?.Cancel();
        oldPlayCts?.Dispose();
        var startFadeIn = false;

        _isPausing = false;

        // Immediately broadcast state change so UI Play/Pause button switches
        // instantly to play (and registers that IsPausing is now false)
        _dispatcherService.TryEnqueue(() =>
        {
            if (!_isDisposed) StateChanged?.Invoke();
        });

        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_currentMedia is not null)
            {
                if (_isDisposed) return;

                if (_isFadeOnPlayPauseEnabled)
                {
                    // Silence before playing so fade-in starts from zero.
                    _isFading = true;
                    _mediaPlayer.SetVolume(0);
                }
                else
                {
                    // Ensure VLC volume matches user volume; a prior fade-out could have left it at 0.
                    _mediaPlayer.SetVolume((int)Math.Clamp(_userVolume * 100, 0, 100));
                }

                // LibVLC 4 queues the start command and returns immediately.
                _isExplicitStop = false;
                _mediaPlayer.SetRate(_playbackRate);
                if (_mediaPlayer.Play())
                {
                    startFadeIn = _isFadeOnPlayPauseEnabled;
                }
                else
                {
                    _isExplicitStop = true;
                    _isFading = false;
                    _mediaPlayer.SetVolume((int)Math.Clamp(_userVolume * 100, 0, 100));
                    _logger.LogWarning("LibVLC rejected the play command. Last error: {LastError}",
                        _libVlc?.LastLibVLCError);
                }
            }
            else
            {
                _logger.LogWarning("Play command received, but no media is loaded.");
            }
        }
        finally
        {
            _vlcOperationLock.Release();
        }

        if (startFadeIn)
            _ = FadeAsync(0.0, _userVolume, TimeSpan.FromMilliseconds(_fadeInDurationMs), fadeCt);
    }

    public async Task PauseAsync()
    {
        if (_isDisposed || !_isInitialized || _mediaPlayer is null) return;

        if (_isFadeOnPlayPauseEnabled)
        {
            var newPauseCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            var fadeCt = newPauseCts.Token;
            var oldPauseCts = Interlocked.Exchange(ref _fadeCts, newPauseCts);
            oldPauseCts?.Cancel();
            oldPauseCts?.Dispose();

            _isPausing = true;

            // Fire and forget the fade-out and pause so the UI command completes immediately
            _ = FadeAndPauseAsync(fadeCt, _userVolume);
            return;
        }

        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || _mediaPlayer is null) return;

            if (_mediaPlayer.CanPause)
                _mediaPlayer.Pause();
            else
                _logger.LogWarning("Pause command received, but player cannot be paused in its current state.");
        }
        finally
        {
            _vlcOperationLock.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_isDisposed || !_isInitialized || _mediaPlayer is null) return;

        _logger.LogDebug("Stop command received.");

        // Cancel any active fade immediately so audio stops without delay
        CancelFade();

        // Mark as explicit stop to prevent false PlaybackEnded in state changed handler
        _isExplicitStop = true;

        // Cancel any pending load — no point loading media we're about to stop
        var pendingLoad = Interlocked.Exchange(ref _loadCts, null);
        pendingLoad?.Cancel();
        pendingLoad?.Dispose();

        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || _mediaPlayer is null) return;

            // LibVLC 4 stop is asynchronous. Wait for the native Stopped event before
            // releasing the media so a subsequent load cannot race the old pipeline.
            await StopNativePlaybackAsync(_mediaPlayer).ConfigureAwait(false);
            _currentSong = null;

            try
            {
                _mediaPlayer.Media = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to detach media after stopping LibVLC playback.");
            }

            DisposeCurrentMedia("stop");
        }
        finally
        {
            _vlcOperationLock.Release();
        }

        if (!_isDisposed) _mediaControls.Clear();
    }

    private async Task StopNativePlaybackAsync(MediaPlayer player)
    {
        // Upstream StopAsync waits for Stopped even when Stop returns false.
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStopped(object? sender, EventArgs args) => stopped.TrySetResult();

        player.Stopped += OnStopped;
        try
        {
            if (!player.Stop())
            {
                _logger.LogTrace("LibVLC stop was a no-op because no playback pipeline was active.");
                return;
            }

            try
            {
                await stopped.Task.WaitAsync(NativeStopTimeout, _disposeCts.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timed out after {TimeoutMs} ms waiting for LibVLC to stop.",
                    NativeStopTimeout.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                _logger.LogDebug("Stopped waiting for LibVLC because the audio service is being disposed.");
            }
        }
        finally
        {
            player.Stopped -= OnStopped;
        }
    }

    private void DisposeCurrentMedia(string context)
    {
        var media = _currentMedia;
        _currentMedia = null;
        if (media is null) return;

        try
        {
            media.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing media during {Context}.", context);
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_isDisposed || !_isInitialized) return;

        if (position < TimeSpan.Zero)
        {
            _logger.LogWarning("Seek command received with negative position {Position}. Clamping to Zero.", position);
            position = TimeSpan.Zero;
        }

        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || _mediaPlayer is null) return;
            if (_mediaPlayer.IsSeekable)
                _mediaPlayer.SeekTo(position, true);
            else
                _logger.LogWarning("Seek command received, but media is not seekable.");
        }
        finally
        {
            _vlcOperationLock.Release();
        }
    }

    private static TimeSpan FromVlcMicroseconds(long microseconds)
    {
        if (microseconds <= 0) return TimeSpan.Zero;

        const long ticksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        return microseconds > TimeSpan.MaxValue.Ticks / ticksPerMicrosecond
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(microseconds * ticksPerMicrosecond);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(_disposeTask.Value);

    private async Task DisposeCoreAsync()
    {
        _isDisposed = true;
        _disposeCts.Cancel();

        var loadCts = Interlocked.Exchange(ref _loadCts, null);
        loadCts?.Cancel();
        loadCts?.Dispose();
        CancelFade();

        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _mediaControls.PlayPressed -= OnMediaControlsPlayPressed;
            _mediaControls.PausePressed -= OnMediaControlsPausePressed;
        }
        finally
        {
            try
            {
                DisposeCurrentMedia("audio service disposal");
                await Task.Run(DisposeNativeComponents).ConfigureAwait(false);
            }
            finally
            {
                // Queued operations still need to acquire and release this managed gate before exiting.
                _vlcOperationLock.Release();
            }
        }
        GC.SuppressFinalize(this);
    }

    private void DisposeNativeComponents()
    {
        _nativeLifetimeLock.EnterWriteLock();
        try
        {
            _isInitialized = false;
            using var libVlc = _libVlc;
            _libVlc = null;
            using var equalizer = _equalizer;
            _equalizer = null;
            using var player = _mediaPlayer;
            _mediaPlayer = null;

            if (libVlc is not null) libVlc.Log -= OnLibVlcLog;
            if (player is not null)
            {
                player.PositionChanged -= OnMediaPlayerPositionChanged;
                player.Playing -= OnMediaPlayerStateChanged;
                player.Paused -= OnMediaPlayerStateChanged;
                player.Stopped -= OnMediaPlayerStateChanged;
                player.EncounteredError -= OnMediaPlayerEncounteredError;
                player.MediaChanged -= OnMediaPlayerMediaChanged;
                player.LengthChanged -= OnMediaPlayerLengthChanged;
                player.VolumeChanged -= OnMediaPlayerVolumeChanged;
                player.Muted -= OnMediaPlayerMuteChanged;
                player.Unmuted -= OnMediaPlayerMuteChanged;
            }

            player?.Stop();
        }
        finally
        {
            _nativeLifetimeLock.ExitWriteLock();
        }
    }
}
