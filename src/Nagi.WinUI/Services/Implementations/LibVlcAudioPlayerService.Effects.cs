using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using System.Threading;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Volume, mute, fades, equalizer and ReplayGain preamp.
/// </summary>
public sealed partial class LibVlcAudioPlayerService
{
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

    public async Task SetVolumeAsync(double volume)
    {
        if (_isDisposed) return;

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
}
