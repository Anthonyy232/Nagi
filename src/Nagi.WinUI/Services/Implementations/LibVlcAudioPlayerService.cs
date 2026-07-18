using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using LibVLCSharp;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using System.Threading;
using Nagi.WinUI.Services.Abstractions;
using WinMediaPlayback = Windows.Media.Playback;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Implements <see cref="IAudioPlayer" /> using LibVLCSharp for robust audio playback
///     and manual integration with the System Media Transport Controls (SMTC).
///     Uses lazy initialization to avoid blocking app startup.
/// </summary>
public sealed class LibVlcAudioPlayerService : IAudioPlayer, IDisposable
{
    private static readonly TimeSpan NativeStopTimeout = TimeSpan.FromSeconds(5);

    private readonly IAppInfoService _appInfoService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<LibVlcAudioPlayerService> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _vlcOperationLock = new(1, 1);
    private CancellationTokenSource? _loadCts;

    // Lazy initialization support
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private Task? _initTask;
    private volatile bool _isInitialized;

    // Deferred LibVLC components (nullable until initialized)
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Equalizer? _equalizer;
    private WinMediaPlayback.MediaPlayer? _dummyMediaPlayer;

    private Media? _currentMedia;
    private Song? _currentSong;
    private SystemMediaTransportControls? _smtc;

    private bool _isDisposed;
    private bool _isExplicitStop; // Prevents false PlaybackEnded events on user/error stop
    private double _replayGainOffset; // Separate tracking of ReplayGain adjustment

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

    // Tracks if we are currently fading out to pause, so IsPlaying can return false early
    private volatile bool _isPausing;

    /// <summary>
    ///     Creates a new instance. LibVLC initialization is deferred until first use.
    /// </summary>
    public LibVlcAudioPlayerService(
        IDispatcherService dispatcherService,
        IAppInfoService appInfoService,
        ILogger<LibVlcAudioPlayerService> logger)
    {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _appInfoService = appInfoService ?? throw new ArgumentNullException(nameof(appInfoService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void EnsureInitialized()
    {
        // Fast-path: already initialized or disposed
        if (_isInitialized || _isDisposed) return;

        // Use Task.Run to execute initialization on thread pool, avoiding potential
        // deadlock if called from UI thread while initialization needs to marshal back to UI.
        // This is safer than direct .GetAwaiter().GetResult() which can deadlock.
        Task.Run(() => EnsureInitializedAsync()).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public Task EnsureInitializedAsync()
    {
        // Fast-path: already initialized or disposed
        if (_isInitialized || _isDisposed) return Task.CompletedTask;

        // Fast-path: if initialization is in progress, await the existing task
        var task = _initTask;
        if (task is not null) return task;

        return EnsureInitializedCoreAsync();
    }

    private async Task EnsureInitializedCoreAsync()
    {
        await _initSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            // Double-check after acquiring semaphore
            if (_isInitialized || _isDisposed) return;

            // If another caller set _initTask while we waited, await it
            if (_initTask is not null)
            {
                await _initTask.ConfigureAwait(false);
                return;
            }

            // Create a task that other callers can await while we hold the semaphore
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _initTask = tcs.Task;

            try
            {
                InitializeLibVlcCore();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                _initTask = null; // Allow retry on failure
                tcs.SetException(ex);
                throw;
            }
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    private void InitializeLibVlcCore()
    {
        _logger.LogDebug("Initializing LibVLC core (deferred).");

        // Register the native library resolver in the same serialized path that creates
        // LibVLC. Callers can reach this method before the background audio warm-up runs
        // (for example, while DI constructs MusicPlaybackService for the taskbar service),
        // so initialization cannot safely live in App.InitializeCoreServicesAsync.
        LibVLCSharp.Core.Initialize();

        // LibVLC explicitly does not guarantee compatibility for constructor options.
        // Keep only the two behaviors that currently lack a suitable public LibVLC API.
        var vlcOptions = new[] { "--no-video", "--no-volume-save" };

        _logger.LogDebug("Initializing LibVLC with options: {VlcOptions}", string.Join(" ", vlcOptions));
        _libVlc = new LibVLC(false, vlcOptions);

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
        _dummyMediaPlayer = new WinMediaPlayback.MediaPlayer { CommandManager = { IsEnabled = false } };

        _equalizer = new Equalizer();
        _equalizer.SetPreamp(_basePreamp);
        _mediaPlayer.SetEqualizer(_equalizer);

        // Register event handlers
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

    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        if (_isDisposed) return;

        var message = string.IsNullOrWhiteSpace(e.FormattedLog) ? e.Message : e.FormattedLog;
        switch (e.Level)
        {
            case LibVLCSharp.LogLevel.Error:
                _logger.LogError("LibVLC [{Module}] {Message}", e.Module, message);
                break;
            case LibVLCSharp.LogLevel.Warning:
                _logger.LogWarning("LibVLC [{Module}] {Message}", e.Module, message);
                break;
            case LibVLCSharp.LogLevel.Notice:
                _logger.LogDebug("LibVLC [{Module}] {Message}", e.Module, message);
                break;
            default:
                _logger.LogTrace("LibVLC [{Module}] {Message}", e.Module, message);
                break;
        }
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
    public event Action? SmtcNextButtonPressed, SmtcPreviousButtonPressed;

    public bool IsPlaying => !_isDisposed && _isInitialized && !_isPausing && (_mediaPlayer?.IsPlaying ?? false);
    public TimeSpan CurrentPosition => _isDisposed || !_isInitialized
        ? TimeSpan.Zero
        : FromVlcMicroseconds(_mediaPlayer?.Time ?? 0);
    public TimeSpan Duration => _isDisposed || !_isInitialized
        ? TimeSpan.Zero
        : FromVlcMicroseconds(_mediaPlayer?.Length ?? 0);
    public double Volume => _isDisposed || !_isInitialized ? 0 : _userVolume;
    public bool IsMuted => !_isDisposed && _isInitialized && (_mediaPlayer?.Mute ?? false);

    public void InitializeSmtc()
    {
        if (_isDisposed) return;
        EnsureInitialized();
        if (_isDisposed || _dummyMediaPlayer is null) return;

        try
        {
            _logger.LogDebug("Initializing System Media Transport Controls (SMTC).");
            _smtc = _dummyMediaPlayer.SystemMediaTransportControls;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = false;
            _smtc.IsPreviousEnabled = false;
            _smtc.ButtonPressed += OnSmtcButtonPressed;
            _smtc.IsEnabled = true;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            _logger.LogDebug("SMTC initialized successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to initialize SMTC. System media controls will be unavailable.");
        }
    }

    public void UpdateSmtcButtonStates(bool canNext, bool canPrevious)
    {
        if (_isDisposed || _smtc is null) return;
        try
        {
            _smtc.IsNextEnabled = canNext;
            _smtc.IsPreviousEnabled = canPrevious;
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80000013)) // RO_E_CLOSED
        {
            _logger.LogDebug("SMTC already disposed, ignoring button state update.");
        }
    }

    public async Task LoadAsync(Song song)
    {
        if (_isDisposed) return;
        EnsureInitialized();
        if (_isDisposed || _mediaPlayer is null) return;

        // Cancel any previous in-flight load immediately (cancel-and-supersede pattern).
        // When user rapidly skips tracks, only the final destination's VLC work executes.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        _currentSong = song;

        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // If a newer LoadAsync call superseded us while we waited for the lock, skip entirely.
            token.ThrowIfCancellationRequested();

            _logger.LogDebug("Loading media for song '{SongTitle}' from path: {FilePath}", song.Title,
                song.FilePath);

            // Offload to thread pool to prevent blocking the UI thread.
            // VLC's Media constructor and Media setter internally parse file headers
            // and initialize demuxers, which can block for 2-3 seconds on large FLAC files.
            await Task.Run(() =>
            {
                if (_isDisposed) return;

                DisposeCurrentMedia("replacement load");

                // Create new media and store reference
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
            var errorMessage = string.Format(Resources.Strings.Player_Error_LoadFailed, song.Title);
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
        EnsureInitialized();
        if (_isDisposed || _mediaPlayer is null) return;

        // Cancel any ongoing fade (e.g. a fade-out from a previous pause that didn't complete)
        var newPlayCts = new CancellationTokenSource();
        var oldPlayCts = Interlocked.Exchange(ref _fadeCts, newPlayCts);
        oldPlayCts?.Cancel();
        oldPlayCts?.Dispose();
        var fadeCt = newPlayCts.Token;
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
                if (_mediaPlayer.Play())
                {
                    startFadeIn = _isFadeOnPlayPauseEnabled;
                }
                else
                {
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
            var newPauseCts = new CancellationTokenSource();
            var oldPauseCts = Interlocked.Exchange(ref _fadeCts, newPauseCts);
            oldPauseCts?.Cancel();
            oldPauseCts?.Dispose();
            var fadeCt = newPauseCts.Token;

            _isPausing = true;

            // Fire and forget the fade-out and pause so the UI command completes immediately
            _ = FadeAndPauseAsync(newPauseCts, _userVolume);
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

    private async Task FadeAndPauseAsync(CancellationTokenSource cts, double targetUserVolume)
    {
        var fadeCt = cts.Token;
        try
        {
            // Immediately broadcast state change so UI Play/Pause button switches
            // instantly, without waiting for the fade to finish.
            _dispatcherService.TryEnqueue(() =>
            {
                if (!_isDisposed) StateChanged?.Invoke();
            });

            // Read the actual VLC volume as the starting point rather than _userVolume.
            // If a fade-in was canceled mid-way (rapid play→pause), starting from _userVolume
            // would cause VLC to briefly ramp up before fading out — an audible blip.
            double fromVolume = Math.Clamp((_mediaPlayer?.Volume ?? 0) / 100.0, 0.0, targetUserVolume);
            await FadeAsync(fromVolume, 0.0, TimeSpan.FromMilliseconds(_fadeOutDurationMs), fadeCt).ConfigureAwait(false);

            if (_isDisposed || _mediaPlayer is null || fadeCt.IsCancellationRequested) return;

            await _vlcOperationLock.WaitAsync(fadeCt).ConfigureAwait(false);
            try
            {
                if (_isDisposed || _mediaPlayer is null || fadeCt.IsCancellationRequested) return;

                if (_mediaPlayer.CanPause)
                    _mediaPlayer.Pause();
            }
            finally
            {
                _vlcOperationLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected if superseded by Play/Stop/Load.
            // Clear the pausing flag since this pause attempt was aborted.
            _isPausing = false;
        }
    }

    public async Task StopAsync()
    {
        if (_isDisposed || !_isInitialized || _mediaPlayer is null) return;

        _logger.LogDebug("Stop command received.");

        // Cancel any active fade immediately so audio stops without delay
        var oldStopCts = Interlocked.Exchange(ref _fadeCts, null);
        oldStopCts?.Cancel();
        oldStopCts?.Dispose();
        _isFading = false;
        _isPausing = false;

        // Mark as explicit stop to prevent false PlaybackEnded in state changed handler
        _isExplicitStop = true;

        // Cancel any pending load — no point loading media we're about to stop
        _loadCts?.Cancel();

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

        if (_smtc is not null && !_isDisposed)
        {
            try
            {
                _smtc.DisplayUpdater.ClearAll();
                _smtc.DisplayUpdater.Update();
                _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80000013)) // RO_E_CLOSED
            {
                _logger.LogDebug("SMTC already disposed during stop.");
            }
        }
    }

    private async Task StopNativePlaybackAsync(MediaPlayer player)
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStopped(object? sender, EventArgs args) => stopped.TrySetResult(true);

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

    public Task SeekAsync(TimeSpan position)
    {
        if (_isDisposed || !_isInitialized || _mediaPlayer is null) return Task.CompletedTask;

        if (position < TimeSpan.Zero)
        {
            _logger.LogWarning("Seek command received with negative position {Position}. Clamping to Zero.", position);
            position = TimeSpan.Zero;
        }

        if (_mediaPlayer.IsSeekable)
            _mediaPlayer.SeekTo(position, true);
        else
            _logger.LogWarning("Seek command received, but media is not seekable.");
        return Task.CompletedTask;
    }

    private static TimeSpan FromVlcMicroseconds(long microseconds)
    {
        if (microseconds <= 0) return TimeSpan.Zero;

        const long ticksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        return microseconds > TimeSpan.MaxValue.Ticks / ticksPerMicrosecond
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(microseconds * ticksPerMicrosecond);
    }

    public Task SetVolumeAsync(double volume)
    {
        if (_isDisposed) return Task.CompletedTask;
        EnsureInitialized();
        if (_isDisposed || _mediaPlayer is null) return Task.CompletedTask;

        // Ensure volume is a valid finite number
        if (!double.IsFinite(volume))
        {
            _logger.LogWarning("SetVolumeAsync received invalid volume: {Volume}. Ignoring.", volume);
            return Task.CompletedTask;
        }

        // Cancel any in-progress fade so the user's explicit change takes effect immediately
        var oldVolCts = Interlocked.Exchange(ref _fadeCts, null);
        oldVolCts?.Cancel();
        oldVolCts?.Dispose();
        _isFading = false;

        _userVolume = Math.Clamp(volume, 0.0, 1.0);
        _mediaPlayer.SetVolume((int)Math.Clamp(_userVolume * 100, 0, 100));
        return Task.CompletedTask;
    }

    public void SetFadeOnPlayPauseEnabled(bool isEnabled)
    {
        _isFadeOnPlayPauseEnabled = isEnabled;
    }

    public void SetFadeInDuration(int durationMs)
    {
        _fadeInDurationMs = durationMs;
    }

    public void SetFadeOutDuration(int durationMs)
    {
        _fadeOutDurationMs = durationMs;
    }

    public Task SetMuteAsync(bool isMuted)
    {
        if (_isDisposed || !_isInitialized || _mediaPlayer is null) return Task.CompletedTask;

        _mediaPlayer.Mute = isMuted;
        return Task.CompletedTask;
    }

    public IReadOnlyList<(uint Index, float Frequency)> GetEqualizerBands()
    {
        if (_isDisposed) return Array.Empty<(uint, float)>();
        EnsureInitialized();

        if (_isDisposed || _equalizer is null)
        {
            _logger.LogWarning("GetEqualizerBands called but equalizer is unavailable (disposed: {IsDisposed}, initialized: {IsInitialized}).", _isDisposed, _isInitialized);
            return Array.Empty<(uint, float)>();
        }

        var bandCount = _equalizer.BandCount;
        var bands = new List<(uint, float)>();
        for (uint i = 0; i < bandCount; i++) bands.Add((i, _equalizer.BandFrequency(i)));
        return bands;
    }

    public bool ApplyEqualizerSettings(EqualizerSettings settings)
    {
        if (_isDisposed || settings == null || !_isInitialized || _equalizer is null || _mediaPlayer is null)
        {
            if (settings == null) _logger.LogWarning("ApplyEqualizerSettings called with null settings. Aborting.");
            return false;
        }

        // Store the user's base preamp setting
        _basePreamp = settings.Preamp;

        // Apply combined preamp (base + ReplayGain offset)
        ApplyCombinedPreamp();

        var bandCount = _equalizer.BandCount;
        for (var i = 0; i < settings.BandGains.Count; i++)
            if (i < bandCount)
            {
                // LibVLC equalizer gain range: -20 to +20 dB
                var gain = Math.Clamp(settings.BandGains[i], -20.0f, 20.0f);
                _equalizer.SetAmp(gain, (uint)i);
            }

        var success = _mediaPlayer.SetEqualizer(_equalizer);
        _logger.LogDebug("Re-applied equalizer to MediaPlayer. Success: {Success}", success);
        return success;
    }

    public Task SetReplayGainAsync(double gainDb)
    {
        if (_isDisposed || !_isInitialized) return Task.CompletedTask;

        _replayGainOffset = gainDb;
        ApplyCombinedPreamp();
        _logger.LogDebug("Applied ReplayGain offset: {GainDb} dB (combined preamp applied)", gainDb);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Applies the combined preamp value from user EQ settings and ReplayGain offset.
    /// </summary>
    private void ApplyCombinedPreamp()
    {
        if (_isDisposed || !_isInitialized || _equalizer is null || _mediaPlayer is null) return;

        // VLC equalizer preamp range: -20 to +20 dB
        // Combine user's base preamp with ReplayGain offset
        var combinedPreamp = (float)Math.Clamp(_basePreamp + _replayGainOffset, -20.0, 20.0);
        _equalizer.SetPreamp(combinedPreamp);
        _mediaPlayer.SetEqualizer(_equalizer);
    }

    /// <summary>
    ///     Ramps the VLC player volume from <paramref name="fromVolume"/> to <paramref name="toVolume"/>
    ///     over <paramref name="duration"/>, suppressing <see cref="VolumeChanged"/> events throughout.
    ///     Cancelling <paramref name="ct"/> aborts the ramp immediately.
    /// </summary>
    private async Task FadeAsync(double fromVolume, double toVolume, TimeSpan duration, CancellationToken ct)
    {
        if (_isDisposed || _mediaPlayer is null) return;

        if (duration <= TimeSpan.Zero)
        {
            _mediaPlayer?.SetVolume((int)Math.Clamp(toVolume * 100, 0, 100));
            _isFading = false;
            return;
        }

        const int stepIntervalMs = 10;
        int steps = Math.Max(1, (int)(duration.TotalMilliseconds / stepIntervalMs));
        _isFading = true;
        try
        {
            double delta = toVolume - fromVolume;

            for (int i = 1; i <= steps; i++)
            {
                if (ct.IsCancellationRequested || _isDisposed) break;
                // Equal-power curve: perceptually linear throughout the ramp.
                // A linear ramp sounds like it snaps on at the end (fade-in) or cuts off
                // at the start (fade-out) because human hearing is logarithmic.
                double t = (double)i / steps;
                double vol = fromVolume + delta * Math.Sin(t * Math.PI / 2.0);
                _mediaPlayer?.SetVolume((int)Math.Clamp(vol * 100, 0, 100));
                try { await Task.Delay(stepIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            // Snap to target if we completed without cancellation
            if (!ct.IsCancellationRequested && !_isDisposed)
                _mediaPlayer?.SetVolume((int)Math.Clamp(toVolume * 100, 0, 100));
        }
        finally
        {
            // Only clear _isFading if this fade ran to completion.
            // If canceled, the caller that canceled us (StopAsync, SetVolumeAsync) sets
            // _isFading = false itself, or a new FadeAsync is already running and owns the flag.
            // Clearing here on cancellation would race with the new fade's _isFading = true.
            if (!ct.IsCancellationRequested)
                _isFading = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        // Signal disposal intent immediately so any in-progress initialization can check
        _disposeCts.Cancel();

        // Cancel any pending load or fade operations
        _loadCts?.Cancel();
        _fadeCts?.Cancel();
        _fadeCts?.Dispose();

        // Wait for semaphore to ensure we don't race with initialization.
        // Use a reasonable timeout to prevent indefinite blocking, but if timeout occurs,
        // we still proceed with disposal since _isDisposed flag will be set.
        var acquiredInit = _initSemaphore.Wait(millisecondsTimeout: 5000);
        if (!acquiredInit)
        {
            _logger.LogWarning("Dispose timed out waiting for initialization semaphore. Proceeding with disposal.");
        }

        // Wait for any in-flight VLC operations to complete before tearing down
        var acquiredVlc = _vlcOperationLock.Wait(millisecondsTimeout: 5000);
        if (!acquiredVlc)
        {
            _logger.LogWarning("Dispose timed out waiting for VLC operation lock. Proceeding with disposal.");
        }

        try
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _logger.LogDebug("Disposing LibVlcAudioPlayerService.");

            // Only cleanup if we were initialized
            if (_isInitialized && _mediaPlayer is not null)
            {
                _mediaPlayer.PositionChanged -= OnMediaPlayerPositionChanged;
                _mediaPlayer.Playing -= OnMediaPlayerStateChanged;
                _mediaPlayer.Paused -= OnMediaPlayerStateChanged;
                _mediaPlayer.Stopped -= OnMediaPlayerStateChanged;
                _mediaPlayer.EncounteredError -= OnMediaPlayerEncounteredError;
                _mediaPlayer.MediaChanged -= OnMediaPlayerMediaChanged;
                _mediaPlayer.LengthChanged -= OnMediaPlayerLengthChanged;
                _mediaPlayer.VolumeChanged -= OnMediaPlayerVolumeChanged;
                _mediaPlayer.Muted -= OnMediaPlayerMuteChanged;
                _mediaPlayer.Unmuted -= OnMediaPlayerMuteChanged;
            }

            if (_smtc is not null)
            {
                try
                {
                    _smtc.ButtonPressed -= OnSmtcButtonPressed;
                    _smtc.IsEnabled = false;
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80000013)) // RO_E_CLOSED
                {
                    _logger.LogDebug("SMTC already disposed during cleanup.");
                }
            }

            // Release Nagi's media reference. MediaPlayer retains its own reference until
            // it is disposed below, so this ordering is safe even during shutdown.
            DisposeCurrentMedia("audio service disposal");

            // Dispose LibVLC components if initialized
            if (_isInitialized)
            {
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
                _equalizer?.Dispose();
                if (_libVlc is not null) _libVlc.Log -= OnLibVlcLog;
                _libVlc?.Dispose();
            }

            try
            {
                _dummyMediaPlayer?.Dispose();
            }
            catch (System.Runtime.InteropServices.COMException comEx) when (comEx.HResult == unchecked((int)0x80000013)) // RO_E_CLOSED
            {
                // Expected during shutdown when COM object is already closed - safe to ignore
                _logger.LogTrace("Dummy media player already closed (RO_E_CLOSED)");
            }
            catch (Exception ex)
            {
                // Unexpected exception - log as warning so it's visible but doesn't crash
                _logger.LogWarning(ex, "Unexpected error disposing dummy media player");
            }
        }
        finally
        {
            // Always release semaphores if we acquired them, then dispose
            if (acquiredVlc)
            {
                _vlcOperationLock.Release();
            }
            if (acquiredInit)
            {
                _initSemaphore.Release();
            }
            _vlcOperationLock.Dispose();
            _initSemaphore.Dispose();
            _loadCts?.Dispose();
            _disposeCts.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void OnMediaPlayerMediaChanged(object? sender, MediaPlayerMediaChangedEventArgs e)
    {
        if (_isDisposed) return;
        if (e.Media is not null)
        {
            _dispatcherService.TryEnqueue(() =>
            {
                if (_isDisposed) return;
                MediaOpened?.Invoke();
            });
            _ = UpdateSmtcDisplayAsync();
        }
    }

    private void OnMediaPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (_isDisposed) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            DurationChanged?.Invoke();
        });
    }

    private void OnMediaPlayerEncounteredError(object? sender, EventArgs e)
    {
        if (_isDisposed || _libVlc is null) return;

        // Mark as explicit stop to prevent double PlaybackEnded (error handler fires it below)
        _isExplicitStop = true;

        var lastVlcError = _libVlc.LastLibVLCError;
        var errorMessage = string.IsNullOrEmpty(lastVlcError)
            ? Resources.Strings.Player_Error_LibVLC_Unspecified
            : string.Format(Resources.Strings.Player_Error_LibVLC_WithDetails, lastVlcError);
        _logger.LogError("Playback error occurred. {ErrorMessage}", errorMessage);
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            ErrorOccurred?.Invoke(errorMessage);
            PlaybackEnded?.Invoke();
        });
    }

    private void OnMediaPlayerStateChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || _mediaPlayer is null) return;

        // Capture volatile fields before enqueueing to prevent TOCTOU races.
        // Between the outer check and lambda execution, Dispose/Stop could null these fields.
        var currentSong = _currentSong;
        var isExplicitStop = _isExplicitStop;

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            StateChanged?.Invoke();

            // Only fire PlaybackEnded for natural end of playback, not explicit stops
            if (_mediaPlayer?.State == VLCState.Stopped && currentSong is not null && !isExplicitStop)
            {
                _logger.LogDebug("Detected natural end of playback for song '{SongTitle}'.", currentSong.Title);
                PlaybackEnded?.Invoke();
            }

            // Reset flag when playback starts
            if (_mediaPlayer?.State == VLCState.Playing)
            {
                _isExplicitStop = false;
                _isPausing = false;
            }
        });
        UpdateSmtcPlaybackStatus();
    }

    private void OnMediaPlayerPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e)
    {
        if (_isDisposed) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            PositionChanged?.Invoke();
        });
    }

    private void OnMediaPlayerVolumeChanged(object? sender, MediaPlayerVolumeChangedEventArgs e)
    {
        if (_isDisposed || _isFading) return;

        // LibVLC 4 uses the Windows audio-session volume. A change made in Windows Settings
        // therefore arrives here just like a change made through Nagi. Keep our user-volume
        // snapshot in sync so the next track does not restore a stale value over the user's
        // Windows mixer adjustment.
        var reportedVolume = _mediaPlayer?.Volume ?? -1;
        if (reportedVolume >= 0)
            Interlocked.Exchange(ref _userVolume, Math.Clamp(reportedVolume / 100.0, 0.0, 1.0));

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed || _isFading) return;
            VolumeChanged?.Invoke();
        });
    }

    private void OnMediaPlayerMuteChanged(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            VolumeChanged?.Invoke();
        });
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (_isDisposed) return;
        _logger.LogDebug("SMTC button pressed: {Button}", args.Button);
        _ = Task.Run(async () =>
        {
            try
            {
                await _dispatcherService.EnqueueAsync(async () =>
                {
                    if (_isDisposed) return;
                    switch (args.Button)
                    {
                        case SystemMediaTransportControlsButton.Play: await PlayAsync(); break;
                        case SystemMediaTransportControlsButton.Pause: await PauseAsync(); break;
                        case SystemMediaTransportControlsButton.Next: SmtcNextButtonPressed?.Invoke(); break;
                        case SystemMediaTransportControlsButton.Previous: SmtcPreviousButtonPressed?.Invoke(); break;
                    }
                });
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80000013)) // RO_E_CLOSED
            {
                _logger.LogDebug("SMTC button handler interrupted during disposal.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling SMTC button press for button {Button}.", args.Button);
            }
        });
    }

    private void UpdateSmtcPlaybackStatus()
    {
        if (_isDisposed || _smtc is null || _mediaPlayer is null) return;
        var newStatus = _mediaPlayer.State switch
        {
            VLCState.Playing => MediaPlaybackStatus.Playing,
            VLCState.Paused => MediaPlaybackStatus.Paused,
            VLCState.Stopped or VLCState.Error => MediaPlaybackStatus.Stopped,
            VLCState.Opening or VLCState.Stopping => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Closed
        };

        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed || _smtc is null) return;
            try
            {
                if (_smtc.PlaybackStatus != newStatus)
                {
                    _logger.LogDebug("Updating SMTC PlaybackStatus from {OldStatus} to {NewStatus}",
                        _smtc.PlaybackStatus, newStatus);
                    _smtc.PlaybackStatus = newStatus;
                }
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80000013)) // RO_E_CLOSED
            {
                _logger.LogDebug("SMTC already disposed during playback status update.");
            }
        });
    }

    private async Task UpdateSmtcDisplayAsync()
    {
        // Capture _currentSong to prevent TOCTOU race - field could become null during async operations
        var currentSong = _currentSong;
        if (_isDisposed || _smtc is null || currentSong is null) return;

        try
        {
            _logger.LogDebug("Updating SMTC display for track '{SongTitle}'.", currentSong.Title);
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = currentSong.Title ?? string.Empty;
            updater.MusicProperties.Artist = currentSong.ArtistName;
            updater.MusicProperties.AlbumArtist =
                currentSong.Album?.ArtistName ?? currentSong.ArtistName;
            updater.MusicProperties.AlbumTitle = currentSong.Album?.Title ?? Album.UnknownAlbumName;

            updater.Thumbnail = null;

            if (!string.IsNullOrEmpty(currentSong.AlbumArtUriFromTrack))
                try
                {
                    if (_disposeCts.IsCancellationRequested) return;
                    var albumArtFile = await StorageFile.GetFileFromPathAsync(currentSong.AlbumArtUriFromTrack).AsTask().ConfigureAwait(false);
                    if (_disposeCts.IsCancellationRequested) return;
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(albumArtFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load and set SMTC thumbnail from {AlbumArtPath}",
                        currentSong.AlbumArtUriFromTrack);
                }

            if (!_disposeCts.IsCancellationRequested)
                updater.Update();
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80000013)) // RO_E_CLOSED
        {
            _logger.LogDebug("SMTC already disposed during display update.");
        }
    }
}
