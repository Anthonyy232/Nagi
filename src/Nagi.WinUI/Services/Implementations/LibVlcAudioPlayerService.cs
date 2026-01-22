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
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<LibVlcAudioPlayerService> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    
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
    
    // Default preamp of 10.0f is a safe neutral value within VLC's -20 to +20 dB range.
    // This gets overwritten by Equalizer.Preamp (typically 12.0f) once LibVLC initializes.
    private float _basePreamp = 10.0f;

    /// <summary>
    ///     Creates a new instance. LibVLC initialization is deferred until first use.
    /// </summary>
    public LibVlcAudioPlayerService(IDispatcherService dispatcherService, ILogger<LibVlcAudioPlayerService> logger)
    {
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _logger = logger;
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

        var vlcOptions = new[]
        {
            "--no-video", "--no-spu", "--no-osd", "--no-stats", "--ignore-config",
            "--no-one-instance", "--no-lua", "--verbose=-1", "--audio-filter=equalizer",
            "--demux=avcodec"
        };

        _logger.LogDebug("Initializing LibVLC with options: {VlcOptions}", string.Join(" ", vlcOptions));
        _libVlc = new LibVLC(false, vlcOptions);
        _mediaPlayer = new MediaPlayer(_libVlc);
        _dummyMediaPlayer = new WinMediaPlayback.MediaPlayer { CommandManager = { IsEnabled = false } };

        _equalizer = new Equalizer();
        _basePreamp = _equalizer.Preamp;
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

    public event Action? PlaybackEnded, PositionChanged, StateChanged, VolumeChanged, MediaOpened, DurationChanged;
    public event Action<string>? ErrorOccurred;
    public event Action? SmtcNextButtonPressed, SmtcPreviousButtonPressed;

    public bool IsPlaying => !_isDisposed && _isInitialized && (_mediaPlayer?.IsPlaying ?? false);
    public TimeSpan CurrentPosition => _isDisposed || !_isInitialized ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_mediaPlayer?.Time ?? 0);
    public TimeSpan Duration => _isDisposed || !_isInitialized ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_mediaPlayer?.Length ?? 0);
    public double Volume => _isDisposed || !_isInitialized ? 0 : (_mediaPlayer?.Volume ?? 0) / 100.0;
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

    public Task LoadAsync(Song song)
    {
        if (_isDisposed) return Task.CompletedTask;
        EnsureInitialized();
        if (_isDisposed || _mediaPlayer is null) return Task.CompletedTask;
        
        _currentSong = song;
        try
        {
            _logger.LogDebug("Loading media for song '{SongTitle}' from path: {FilePath}", song.Title,
                song.FilePath);

            // Dispose previous media if it exists
            if (_currentMedia != null)
            {
                try
                {
                    _currentMedia.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing previous media");
                }

                _currentMedia = null;
            }

            // Create new media and store reference
            _currentMedia = new Media(new Uri(song.FilePath));
            _mediaPlayer.Media = _currentMedia;
            // Do NOT dispose the media immediately - let it be disposed when no longer needed
        }
        catch (Exception ex)
        {
            var errorMessage = $"Failed to load '{song.Title}'.";
            _logger.LogError(ex, "Failed to load song '{SongTitle}' from path: {FilePath}", song.Title, song.FilePath);
            _dispatcherService.TryEnqueue(() => ErrorOccurred?.Invoke(errorMessage));
            _currentSong = null;

            // Clean up media on error
            if (_currentMedia != null)
            {
                try
                {
                    _currentMedia.Dispose();
                }
                catch (Exception disposeEx)
                {
                    _logger.LogWarning(disposeEx, "Error disposing media after load failure");
                }

                _currentMedia = null;
            }
        }

        return Task.CompletedTask;
    }

    public Task PlayAsync()
    {
        if (_isDisposed) return Task.CompletedTask;
        EnsureInitialized();
        if (_isDisposed || _mediaPlayer is null) return Task.CompletedTask;

        if (_mediaPlayer.Media is not null)
            _mediaPlayer.Play();
        else
            _logger.LogWarning("Play command received, but no media is loaded.");
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        if (_isDisposed || !_isInitialized || _mediaPlayer is null) return Task.CompletedTask;

        if (_mediaPlayer.CanPause)
            _mediaPlayer.Pause();
        else
            _logger.LogWarning("Pause command received, but player cannot be paused in its current state.");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_isDisposed || !_isInitialized || _mediaPlayer is null) return Task.CompletedTask;

        _logger.LogDebug("Stop command received.");
        
        // Mark as explicit stop to prevent false PlaybackEnded in state changed handler
        _isExplicitStop = true;
        _mediaPlayer.Stop();
        _currentSong = null;

        // Clean up current media when stopping
        if (_currentMedia != null)
        {
            try
            {
                _currentMedia.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing media during stop");
            }

            _currentMedia = null;
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

        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        if (_isDisposed || !_isInitialized || _mediaPlayer is null) return Task.CompletedTask;

        if (_mediaPlayer.IsSeekable)
            _mediaPlayer.SetTime((long)position.TotalMilliseconds, true);
        else
            _logger.LogWarning("Seek command received, but media is not seekable.");
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume)
    {
        if (_isDisposed) return Task.CompletedTask;
        EnsureInitialized();
        if (_isDisposed || _mediaPlayer is null) return Task.CompletedTask;

        var vlcVolume = (int)Math.Clamp(volume * 100, 0, 100);
        _mediaPlayer.SetVolume(vlcVolume);
        return Task.CompletedTask;
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
                _equalizer.SetAmp(settings.BandGains[i], (uint)i);

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

    public void Dispose()
    {
        if (_isDisposed) return;

        // Signal disposal intent immediately so any in-progress initialization can check
        _disposeCts.Cancel();

        // Wait for semaphore to ensure we don't race with initialization.
        // Use a reasonable timeout to prevent indefinite blocking, but if timeout occurs,
        // we still proceed with disposal since _isDisposed flag will be set.
        var acquired = _initSemaphore.Wait(millisecondsTimeout: 5000);
        if (!acquired)
        {
            _logger.LogWarning("Dispose timed out waiting for initialization semaphore. Proceeding with disposal.");
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

            // Clean up current media before disposing other components
            if (_currentMedia != null)
            {
                try
                {
                    _currentMedia.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing media during LibVlcAudioPlayerService disposal");
                }

                _currentMedia = null;
            }

            // Dispose LibVLC components if initialized
            if (_isInitialized)
            {
                _equalizer?.Dispose();
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
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
            // Always release semaphore if we acquired it, then dispose it
            if (acquired)
            {
                _initSemaphore.Release();
            }
            _initSemaphore.Dispose();
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
            ? "LibVLC encountered an unspecified error."
            : $"LibVLC Error: {lastVlcError}";
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
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            StateChanged?.Invoke();
            
            // Only fire PlaybackEnded for natural end of playback, not explicit stops
            if (_mediaPlayer?.State == VLCState.Stopped && _currentSong is not null && !_isExplicitStop)
            {
                _logger.LogDebug("Detected natural end of playback for song '{SongTitle}'.", _currentSong.Title);
                PlaybackEnded?.Invoke();
            }
            
            // Reset flag when playback starts
            if (_mediaPlayer?.State == VLCState.Playing)
            {
                _isExplicitStop = false;
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
        if (_isDisposed) return;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
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
            VLCState.Opening or VLCState.Buffering or VLCState.Stopping => MediaPlaybackStatus.Changing,
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
        if (_isDisposed || _smtc is null || _currentSong is null) return;

        try
        {
            _logger.LogDebug("Updating SMTC display for track '{SongTitle}'.", _currentSong.Title);
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = _currentSong.Title ?? string.Empty;
            updater.MusicProperties.Artist = _currentSong.ArtistName;
            updater.MusicProperties.AlbumArtist =
                _currentSong.Album?.ArtistName ?? _currentSong.ArtistName;
            updater.MusicProperties.AlbumTitle = _currentSong.Album?.Title ?? Album.UnknownAlbumName;

            updater.Thumbnail = null;

            if (!string.IsNullOrEmpty(_currentSong.AlbumArtUriFromTrack))
                try
                {
                    if (_disposeCts.IsCancellationRequested) return;
                    var albumArtFile = await StorageFile.GetFileFromPathAsync(_currentSong.AlbumArtUriFromTrack).AsTask().ConfigureAwait(false);
                    if (_disposeCts.IsCancellationRequested) return;
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(albumArtFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load and set SMTC thumbnail from {AlbumArtPath}",
                        _currentSong.AlbumArtUriFromTrack);
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