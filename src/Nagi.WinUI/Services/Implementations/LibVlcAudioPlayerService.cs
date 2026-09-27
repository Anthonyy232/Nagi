using System;
using System.Collections.Generic;
using System.Diagnostics;
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
public sealed class LibVlcAudioPlayerService : IAudioPlayer, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan NativeStopTimeout = TimeSpan.FromSeconds(5);

    private readonly IAppInfoService _appInfoService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<LibVlcAudioPlayerService> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _vlcOperationLock = new(1, 1);
    private readonly ReaderWriterLockSlim _nativeLifetimeLock = new();
    private CancellationTokenSource? _loadCts;

    private readonly Lazy<Task> _disposeTask;
    private volatile bool _isInitialized;

    // Deferred LibVLC components (nullable until initialized)
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Equalizer? _equalizer;
    private WinMediaPlayback.MediaPlayer? _dummyMediaPlayer;

    private Media? _currentMedia;
    private Song? _currentSong;
    private SystemMediaTransportControls? _smtc;

    private volatile bool _isDisposed;
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
        ISettingsService settingsService,
        ILogger<LibVlcAudioPlayerService> logger)
    {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _appInfoService = appInfoService ?? throw new ArgumentNullException(nameof(appInfoService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _disposeTask = new Lazy<Task>(DisposeCoreAsync);
    }

    private void EnsureInitialized()
    {
        if (_isInitialized || _isDisposed) return;
        EnsureInitializedAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized || _isDisposed) return;
        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isInitialized || _isDisposed) return;
            var useWasapiExclusive = await _settingsService.GetWasapiExclusiveModeEnabledAsync().ConfigureAwait(false);
            await Task.Run(() => InitializeLibVlcCore(useWasapiExclusive), _disposeCts.Token).ConfigureAwait(false);
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

    private void InitializeLibVlcCore(bool useWasapiExclusive)
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
        if (useWasapiExclusive) vlcOptions.Add("--wasapi-exclusive");

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
        _dummyMediaPlayer = new WinMediaPlayback.MediaPlayer();
        _dummyMediaPlayer.CommandManager.IsEnabled = false;

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

    public bool IsPlaying => !_isPausing && ReadNative(() => _mediaPlayer?.State == VLCState.Playing, false);
    public TimeSpan CurrentPosition => ReadNative(() => FromVlcMicroseconds(_mediaPlayer?.Time ?? 0), TimeSpan.Zero);
    public TimeSpan Duration => ReadNative(() => FromVlcMicroseconds(_mediaPlayer?.Length ?? 0), TimeSpan.Zero);
    public double Volume => _isDisposed || !_isInitialized ? 0 : _userVolume;
    public bool IsMuted => ReadNative(() => _mediaPlayer?.Mute ?? false, false);

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

    public void InitializeSmtc()
    {
        if (_isDisposed) return;
        EnsureInitialized();
        _vlcOperationLock.Wait();
        try
        {
            if (_isDisposed || _dummyMediaPlayer is null || _smtc is not null) return;
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
        finally
        {
            _vlcOperationLock.Release();
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

    private async Task FadeAndPauseAsync(CancellationToken fadeCt, double targetUserVolume)
    {
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
            double fromVolume;
            await _vlcOperationLock.WaitAsync(fadeCt).ConfigureAwait(false);
            try
            {
                if (_isDisposed || _mediaPlayer is null) return;
                fromVolume = Math.Clamp(_mediaPlayer.Volume / 100.0, 0.0, targetUserVolume);
            }
            finally
            {
                _vlcOperationLock.Release();
            }
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
            // The replacing operation owns the current pausing state.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fade out and pause playback.");
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

    public async Task SetVolumeAsync(double volume)
    {
        if (_isDisposed) return;

        // Ensure volume is a valid finite number
        if (!double.IsFinite(volume))
        {
            _logger.LogWarning("SetVolumeAsync received invalid volume: {Volume}. Ignoring.", volume);
            return;
        }

        await EnsureInitializedAsync().ConfigureAwait(false);
        CancelFade();
        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || _mediaPlayer is null) return;
            _userVolume = Math.Clamp(volume, 0.0, 1.0);
            _mediaPlayer.SetVolume((int)(_userVolume * 100));
        }
        finally
        {
            _vlcOperationLock.Release();
        }
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

    public async Task SetMuteAsync(bool isMuted)
    {
        if (_isDisposed || !_isInitialized) return;
        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isDisposed && _mediaPlayer is not null) _mediaPlayer.Mute = isMuted;
        }
        finally
        {
            _vlcOperationLock.Release();
        }
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

        _vlcOperationLock.Wait();
        try
        {
            if (_isDisposed || _equalizer is null) return Array.Empty<(uint, float)>();
            var bands = new List<(uint, float)>();
            for (uint i = 0; i < _equalizer.BandCount; i++)
                bands.Add((i, _equalizer.BandFrequency(i)));
            return bands;
        }
        finally
        {
            _vlcOperationLock.Release();
        }
    }

    public bool ApplyEqualizerSettings(EqualizerSettings settings)
    {
        if (_isDisposed || !_isInitialized) return false;
        _vlcOperationLock.Wait();
        try
        {
            if (_isDisposed || _equalizer is null || _mediaPlayer is null) return false;
            _basePreamp = settings.Preamp;
            ApplyCombinedPreamp();
            var bandCount = Math.Min(settings.BandGains.Count, (int)_equalizer.BandCount);
            for (var i = 0; i < bandCount; i++)
                _equalizer.SetAmp(Math.Clamp(settings.BandGains[i], -20.0f, 20.0f), (uint)i);

            return _mediaPlayer.SetEqualizer(_equalizer);
        }
        finally
        {
            _vlcOperationLock.Release();
        }
    }

    public async Task SetReplayGainAsync(double gainDb)
    {
        if (_isDisposed || !_isInitialized) return;
        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed) return;
            _replayGainOffset = gainDb;
            ApplyCombinedPreamp();
            _logger.LogDebug("Applied ReplayGain offset: {GainDb} dB (combined preamp applied)", gainDb);
        }
        finally
        {
            _vlcOperationLock.Release();
        }
    }

    private void CancelFade()
    {
        var cts = Interlocked.Exchange(ref _fadeCts, null);
        cts?.Cancel();
        cts?.Dispose();
        _isFading = false;
        _isPausing = false;
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
        var stopwatch = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        _isFading = true;
        try
        {
            do
            {
                var fraction = duration <= TimeSpan.Zero ? 1.0 : Math.Min(1.0, stopwatch.Elapsed / duration);
                var volume = fromVolume + (toVolume - fromVolume) * Math.Sin(fraction * Math.PI / 2.0);
                await _vlcOperationLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_isDisposed || _mediaPlayer is null) return;
                    _mediaPlayer.SetVolume((int)Math.Clamp(volume * 100, 0, 100));
                }
                finally
                {
                    _vlcOperationLock.Release();
                }
                if (fraction >= 1.0) return;
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            // A replacement fade owns the flag once this token is cancelled.
            if (!ct.IsCancellationRequested) _isFading = false;
        }
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

            try
            {
                _dummyMediaPlayer?.Dispose();
            }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80000013)) // RO_E_CLOSED
            {
                _logger.LogTrace("Dummy media player already closed.");
            }
            finally
            {
                _dummyMediaPlayer = null;
            }
            player?.Stop();
        }
        finally
        {
            _nativeLifetimeLock.ExitWriteLock();
        }
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

        _isExplicitStop = true;

        var lastVlcError = ReadNative(() => _libVlc?.LastLibVLCError, null);
        var errorMessage = string.IsNullOrEmpty(lastVlcError)
            ? Resources.Strings.Player_Error_LibVLC_Unspecified
            : string.Format(Resources.Strings.Player_Error_LibVLC_WithDetails, lastVlcError);
        _logger.LogError("Playback error occurred. {ErrorMessage}", errorMessage);
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            ErrorOccurred?.Invoke(errorMessage);
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
            var state = ReadNative(() => _mediaPlayer?.State, null);

            // Only fire PlaybackEnded for natural end of playback, not explicit stops
            if (state == VLCState.Stopped && currentSong is not null && !isExplicitStop)
            {
                _logger.LogDebug("Detected natural end of playback for song '{SongTitle}'.", currentSong.Title);
                PlaybackEnded?.Invoke();
            }

            // Reset flag when playback starts
            if (state == VLCState.Playing)
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
        var reportedVolume = ReadNative(() => _mediaPlayer?.Volume ?? -1, -1);
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

    private async void OnSmtcButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (_isDisposed) return;
        _logger.LogDebug("SMTC button pressed: {Button}", args.Button);
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
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80000013)) // RO_E_CLOSED
        {
            _logger.LogDebug("SMTC button handler interrupted during disposal.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling SMTC button press for button {Button}.", args.Button);
        }
    }

    private void UpdateSmtcPlaybackStatus()
    {
        if (_isDisposed || _smtc is null || _mediaPlayer is null) return;
        var newStatus = ReadNative(() => _mediaPlayer?.State, null) switch
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
        var currentSong = _currentSong;
        if (_isDisposed || _smtc is null || currentSong is null) return;

        try
        {
            _logger.LogDebug("Updating SMTC display for track '{SongTitle}'.", currentSong.Title);
            RandomAccessStreamReference? thumbnail = null;
            if (!string.IsNullOrEmpty(currentSong.AlbumArtUriFromTrack))
                try
                {
                    var albumArtFile = await StorageFile.GetFileFromPathAsync(currentSong.AlbumArtUriFromTrack)
                        .AsTask(_disposeCts.Token).ConfigureAwait(false);
                    thumbnail = RandomAccessStreamReference.CreateFromFile(albumArtFile);
                }
                catch (OperationCanceledException) when (_isDisposed) { return; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load and set SMTC thumbnail from {AlbumArtPath}",
                        currentSong.AlbumArtUriFromTrack);
                }

            if (_isDisposed || !ReferenceEquals(currentSong, _currentSong)) return;
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = currentSong.Title;
            updater.MusicProperties.Artist = currentSong.ArtistName;
            updater.MusicProperties.AlbumArtist = currentSong.Album?.ArtistName ?? currentSong.ArtistName;
            updater.MusicProperties.AlbumTitle = currentSong.Album?.Title ?? Album.UnknownAlbumName;
            updater.Thumbnail = thumbnail;
            updater.Update();
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80000013)) // RO_E_CLOSED
        {
            _logger.LogDebug("SMTC already disposed during display update.");
        }
    }
}
