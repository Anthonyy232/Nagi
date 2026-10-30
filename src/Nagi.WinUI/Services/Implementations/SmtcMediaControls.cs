using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using WinMediaPlayback = Windows.Media.Playback;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Windows System Media Transport Controls: media keys, the media overlay and the now-playing display.
///     LibVLC plays the audio, so an idle WinRT media player is kept only to own the controls.
/// </summary>
public sealed class SmtcMediaControls : ISystemMediaControls, IDisposable
{
    private const int RO_E_CLOSED = unchecked((int)0x80000013);

    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<SmtcMediaControls> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _lifetimeLock = new();
    private WinMediaPlayback.MediaPlayer? _ownerPlayer;
    private SystemMediaTransportControls? _smtc;
    private int _displayVersion;
    private volatile bool _isDisposed;

    public SmtcMediaControls(IDispatcherService dispatcherService, ILogger<SmtcMediaControls> logger)
    {
        _dispatcherService = dispatcherService;
        _logger = logger;
    }

    public event Action? PlayPressed;
    public event Action? PausePressed;
    public event Action? NextPressed;
    public event Action? PreviousPressed;
    public event Action<TimeSpan>? SeekRequested;

    public void Initialize()
    {
        lock (_lifetimeLock)
        {
            if (_isDisposed || _smtc is not null) return;
            try
            {
                _logger.LogDebug("Initializing System Media Transport Controls (SMTC).");
                _ownerPlayer = new WinMediaPlayback.MediaPlayer();
                _ownerPlayer.CommandManager.IsEnabled = false;
                var smtc = _ownerPlayer.SystemMediaTransportControls;
                smtc.IsPlayEnabled = true;
                smtc.IsPauseEnabled = true;
                smtc.IsNextEnabled = false;
                smtc.IsPreviousEnabled = false;
                smtc.ButtonPressed += OnButtonPressed;
                smtc.PlaybackPositionChangeRequested += OnPlaybackPositionChangeRequested;
                smtc.IsEnabled = true;
                smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                _smtc = smtc;
                _logger.LogDebug("SMTC initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to initialize SMTC. System media controls will be unavailable.");
            }
        }
    }

    public void SetNavigationEnabled(bool canGoNext, bool canGoPrevious)
    {
        var smtc = _smtc;
        if (_isDisposed || smtc is null) return;
        try
        {
            smtc.IsNextEnabled = canGoNext;
            smtc.IsPreviousEnabled = canGoPrevious;
        }
        catch (Exception ex) when (ex.HResult == RO_E_CLOSED)
        {
            _logger.LogDebug("SMTC already disposed, ignoring button state update.");
        }
    }

    public void SetPlaybackStatus(SystemMediaPlaybackStatus status)
    {
        if (_isDisposed || _smtc is null) return;
        var newStatus = status switch
        {
            SystemMediaPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
            SystemMediaPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
            SystemMediaPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
            SystemMediaPlaybackStatus.Changing => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Closed
        };

        _dispatcherService.TryEnqueue(() =>
        {
            var smtc = _smtc;
            if (_isDisposed || smtc is null) return;
            try
            {
                if (smtc.PlaybackStatus == newStatus) return;
                _logger.LogDebug("Updating SMTC PlaybackStatus from {OldStatus} to {NewStatus}",
                    smtc.PlaybackStatus, newStatus);
                smtc.PlaybackStatus = newStatus;
            }
            catch (Exception ex) when (ex.HResult == RO_E_CLOSED)
            {
                _logger.LogDebug("SMTC already disposed during playback status update.");
            }
        });
    }

    public void SetTimeline(TimeSpan position, TimeSpan duration)
    {
        var smtc = _smtc;
        if (_isDisposed || smtc is null) return;
        try
        {
            smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                Position = position,
                MaxSeekTime = duration,
                EndTime = duration
            });
        }
        catch (Exception ex) when (ex.HResult == RO_E_CLOSED)
        {
            _logger.LogDebug("SMTC already disposed during timeline update.");
        }
    }

    public async Task SetNowPlayingAsync(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        var smtc = _smtc;
        if (_isDisposed || smtc is null) return;
        var version = Interlocked.Increment(ref _displayVersion);

        try
        {
            _logger.LogDebug("Updating SMTC display for track '{SongTitle}'.", song.Title);
            RandomAccessStreamReference? thumbnail = null;
            if (!string.IsNullOrEmpty(song.AlbumArtUriFromTrack))
                try
                {
                    var albumArtFile = await StorageFile.GetFileFromPathAsync(song.AlbumArtUriFromTrack)
                        .AsTask(_disposeCts.Token).ConfigureAwait(false);
                    thumbnail = RandomAccessStreamReference.CreateFromFile(albumArtFile);
                }
                catch (OperationCanceledException) when (_isDisposed) { return; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load and set SMTC thumbnail from {AlbumArtPath}",
                        song.AlbumArtUriFromTrack);
                }

            if (_isDisposed || version != Volatile.Read(ref _displayVersion)) return;
            var updater = smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = song.Title;
            updater.MusicProperties.Artist = song.ArtistName;
            updater.MusicProperties.AlbumArtist = song.Album?.ArtistName ?? song.ArtistName;
            updater.MusicProperties.AlbumTitle = song.Album?.Title ?? Album.UnknownAlbumName;
            updater.Thumbnail = thumbnail;
            updater.Update();
        }
        catch (Exception ex) when (ex.HResult == RO_E_CLOSED)
        {
            _logger.LogDebug("SMTC already disposed during display update.");
        }
    }

    public void Clear()
    {
        var smtc = _smtc;
        if (_isDisposed || smtc is null) return;
        Interlocked.Increment(ref _displayVersion);
        try
        {
            smtc.DisplayUpdater.ClearAll();
            smtc.DisplayUpdater.Update();
            smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties());
            smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
        }
        catch (Exception ex) when (ex.HResult == RO_E_CLOSED)
        {
            _logger.LogDebug("SMTC already disposed during stop.");
        }
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }

        _disposeCts.Cancel();
        try
        {
            if (_smtc is not null)
            {
                _smtc.ButtonPressed -= OnButtonPressed;
                _smtc.PlaybackPositionChangeRequested -= OnPlaybackPositionChangeRequested;
                _smtc.IsEnabled = false;
            }

            _ownerPlayer?.Dispose();
        }
        catch (Exception ex) when (ex.HResult == RO_E_CLOSED)
        {
            _logger.LogDebug("SMTC already disposed during cleanup.");
        }
        finally
        {
            _smtc = null;
            _ownerPlayer = null;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (_isDisposed) return;
        _logger.LogDebug("SMTC button pressed: {Button}", args.Button);
        var button = args.Button;
        _dispatcherService.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            var handler = button switch
            {
                SystemMediaTransportControlsButton.Play => PlayPressed,
                SystemMediaTransportControlsButton.Pause => PausePressed,
                SystemMediaTransportControlsButton.Next => NextPressed,
                SystemMediaTransportControlsButton.Previous => PreviousPressed,
                _ => null
            };
            handler?.Invoke();
        });
    }

    private void OnPlaybackPositionChangeRequested(SystemMediaTransportControls sender,
        PlaybackPositionChangeRequestedEventArgs args)
    {
        if (_isDisposed) return;
        var position = args.RequestedPlaybackPosition;
        _logger.LogDebug("SMTC seek requested to {Position}", position);
        _dispatcherService.TryEnqueue(() =>
        {
            if (!_isDisposed) SeekRequested?.Invoke(position);
        });
    }
}
