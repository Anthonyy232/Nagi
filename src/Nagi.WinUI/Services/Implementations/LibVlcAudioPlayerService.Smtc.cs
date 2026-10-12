using System;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using LibVLCSharp;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Windows System Media Transport Controls: buttons, playback status and now-playing display.
/// </summary>
public sealed partial class LibVlcAudioPlayerService
{
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
