using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;

namespace Nagi.Audio.LibVlc;

/// <summary>
///     Choosing and restoring the audio output device.
/// </summary>
public sealed partial class LibVlcAudioPlayerService
{
    private void ApplySavedAudioOutput(string outputDeviceId)
    {
        if (string.IsNullOrEmpty(outputDeviceId) || _mediaPlayer is null) return;
        try
        {
            if (!_mediaPlayer.AudioOutputDeviceEnum.Any(d => d.DeviceIdentifier == outputDeviceId))
            {
                _logger.LogWarning("Saved audio output {DeviceId} is unavailable; using system default.", outputDeviceId);
                return;
            }
            if (_mediaPlayer.SetOutputDevice(outputDeviceId)) return;
            _logger.LogWarning("Could not select saved audio output {DeviceId}; using system default.", outputDeviceId);
            LibVlcAudioOutputInterop.UseSystemDefault(_mediaPlayer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply saved audio output {DeviceId}.", outputDeviceId);
        }
    }

    public async Task<IReadOnlyList<AudioOutputDeviceInfo>> GetAudioOutputDevicesAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || _mediaPlayer is null) return Array.Empty<AudioOutputDeviceInfo>();
            return await Task.Run(() => _mediaPlayer.AudioOutputDeviceEnum
                .Where(d => !string.IsNullOrEmpty(d.DeviceIdentifier))
                .Select(d => new AudioOutputDeviceInfo(d.DeviceIdentifier, d.Description))
                .ToArray()).ConfigureAwait(false);
        }
        finally { _vlcOperationLock.Release(); }
    }

    public async Task<bool> SetAudioOutputDeviceAsync(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _vlcOperationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || _mediaPlayer is null) return false;
            return await Task.Run(() =>
            {
                // A device can disappear between opening the dropdown and choosing it.
                if (deviceId.Length > 0 && !_mediaPlayer.AudioOutputDeviceEnum.Any(d => d.DeviceIdentifier == deviceId))
                    return false;
                return deviceId.Length == 0
                    ? LibVlcAudioOutputInterop.UseSystemDefault(_mediaPlayer)
                    : _mediaPlayer.SetOutputDevice(deviceId);
            }).ConfigureAwait(false);
        }
        finally { _vlcOperationLock.Release(); }
    }
}
