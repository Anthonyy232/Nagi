using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Plays audio files. Events are raised on the UI thread, which the playback service relies on.
/// </summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>
    ///     Occurs when the current media item has finished playing naturally.
    /// </summary>
    event Action? PlaybackEnded;

    event Action? PositionChanged;

    event Action? StateChanged;

    event Action? VolumeChanged;

    event Action<string>? ErrorOccurred;

    event Action? MediaOpened;

    event Action? DurationChanged;

    bool IsPlaying { get; }

    TimeSpan CurrentPosition { get; }

    TimeSpan Duration { get; }

    /// <summary>
    ///     Gets the current volume level, from 0.0 (silent) to 1.0 (maximum).
    /// </summary>
    double Volume { get; }

    bool IsMuted { get; }

    /// <summary>The current output ID, or an empty string for system default routing.</summary>
    string AudioOutputDeviceId { get; }

    /// <summary>
    ///     Asynchronously ensures the audio player is initialized and ready for playback.
    ///     This can be called from a background thread to pre-warm the audio engine
    ///     without blocking the UI thread during app startup. Concurrent calls are serialized.
    /// </summary>
    /// <returns>A task that completes when initialization is done.</returns>
    Task EnsureInitializedAsync();

    /// <summary>Enumerates named outputs; system default is represented separately by an empty ID.</summary>
    Task<IReadOnlyList<AudioOutputDeviceInfo>> GetAudioOutputDevicesAsync();

    /// <summary>
    /// Requests an output change. An empty ID selects the system default.
    /// True means the request was accepted; opening the output stream can still fail asynchronously.
    /// </summary>
    Task<bool> SetAudioOutputDeviceAsync(string deviceId);

    /// <summary>
    ///     Asynchronously loads a song for playback. The returned task completes when the media is successfully opened or has
    ///     failed to load.
    /// <param name="song">The song to load.</param>
    Task LoadAsync(Song song);

    Task PlayAsync();

    Task PauseAsync();

    /// <summary>
    ///     Stops playback and unloads the current media.
    /// </summary>
    Task StopAsync();

    Task SeekAsync(TimeSpan position);

    /// <summary>
    ///     Sets the player's volume.
    /// </summary>
    /// <param name="volume">The volume level, clamped between 0.0 and 1.0.</param>
    Task SetVolumeAsync(double volume);

    Task SetMuteAsync(bool isMuted);

    /// <summary>
    ///     Sets the playback speed while keeping the pitch. 1.0 is normal speed.
    /// </summary>
    Task SetPlaybackRateAsync(double rate);

    bool ApplyEqualizerSettings(EqualizerSettings settings);

    IReadOnlyList<(uint Index, float Frequency)> GetEqualizerBands();

    /// <summary>
    ///     Sets the ReplayGain adjustment for volume normalization.
    /// </summary>
    /// <param name="gainDb">The gain adjustment in decibels. Use 0 for no adjustment.</param>
    Task SetReplayGainAsync(double gainDb);

    /// <summary>
    ///     Enables or disables the fade-in/fade-out effect on play and pause.
    ///     When enabled, the volume ramps smoothly instead of cutting instantly.
    /// </summary>
    void SetFadeOnPlayPauseEnabled(bool isEnabled);

    void SetFadeInDuration(int durationMs);

    void SetFadeOutDuration(int durationMs);
}
