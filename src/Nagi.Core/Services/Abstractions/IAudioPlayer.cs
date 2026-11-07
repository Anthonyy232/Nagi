using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Plays audio files and music server streams. Events are raised on the UI thread, which the playback service relies on.
/// </summary>
/// <remarks>
///     The player starts itself, off the calling thread, on the first call that needs it. <see cref="PauseAsync" />,
///     <see cref="StopAsync" /> and <see cref="SeekAsync" /> do nothing until then, since nothing can be loaded yet.
/// </remarks>
public interface IAudioPlayer : IDisposable
{
    /// <summary>
    ///     Occurs when the current media item has finished playing naturally.
    /// </summary>
    event Action? PlaybackEnded;

    event Action? PositionChanged;

    event Action? StateChanged;

    event Action? VolumeChanged;

    /// <summary>
    ///     Occurs when the current song can't be loaded or played. The player logs the reason itself.
    /// </summary>
    event Action? ErrorOccurred;

    event Action? MediaOpened;

    event Action? DurationChanged;

    bool IsPlaying { get; }

    /// <summary>
    ///     True while the current song is still opening and hasn't started, such as while its music server answers.
    ///     <see cref="StateChanged" /> occurs when this changes.
    /// </summary>
    bool IsOpening { get; }

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
    ///     Starts the player ahead of its first use, so that use doesn't wait for it. Concurrent calls are serialized.
    /// </summary>
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
    /// </summary>
    /// <param name="streamUri">
    ///     Where to stream a song on a music server from, or null to play <see cref="Song.FilePath" />. The URL carries the
    ///     server's credentials, so it must not be logged.
    /// </param>
    Task LoadAsync(Song song, Uri? streamUri = null);

    Task PlayAsync();

    Task PauseAsync();

    /// <summary>
    ///     Stops playback and unloads the current media.
    /// </summary>
    Task StopAsync();

    Task SeekAsync(TimeSpan position);

    /// <param name="volume">The volume level, clamped between 0.0 and 1.0.</param>
    Task SetVolumeAsync(double volume);

    Task SetMuteAsync(bool isMuted);

    /// <summary>
    ///     Sets the playback speed while keeping the pitch. 1.0 is normal speed.
    /// </summary>
    Task SetPlaybackRateAsync(double rate);

    Task ApplyEqualizerSettingsAsync(EqualizerSettings settings);

    Task<IReadOnlyList<(uint Index, float Frequency)>> GetEqualizerBandsAsync();

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
