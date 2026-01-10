using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a low-level audio player responsible for playback control
///     and integration with the System Media Transport Controls (SMTC).
/// </summary>
public interface IAudioPlayer : IDisposable
{
    #region Events

    /// <summary>
    ///     Occurs when the current media item has finished playing naturally.
    /// </summary>
    event Action? PlaybackEnded;

    /// <summary>
    ///     Occurs frequently as the playback position changes.
    /// </summary>
    event Action? PositionChanged;

    /// <summary>
    ///     Occurs when the player's state (e.g., Playing, Paused, Stopped) changes.
    /// </summary>
    event Action? StateChanged;

    /// <summary>
    ///     Occurs when the volume or mute state is changed.
    /// </summary>
    event Action? VolumeChanged;

    /// <summary>
    ///     Occurs when a media playback error is encountered. The string parameter contains the error message.
    /// </summary>
    event Action<string>? ErrorOccurred;

    /// <summary>
    ///     Occurs when new media has been successfully opened and is ready for playback.
    /// </summary>
    event Action? MediaOpened;

    /// <summary>
    ///     Occurs when the duration of the currently loaded media becomes known.
    /// </summary>
    event Action? DurationChanged;

    /// <summary>
    ///     Occurs when the SMTC 'Next' button is pressed by the user.
    ///     This allows the high-level playback service to manage the queue.
    /// </summary>
    event Action? SmtcNextButtonPressed;

    /// <summary>
    ///     Occurs when the SMTC 'Previous' button is pressed by the user.
    ///     This allows the high-level playback service to manage the queue.
    /// </summary>
    event Action? SmtcPreviousButtonPressed;

    #endregion

    #region Properties

    /// <summary>
    ///     Gets a value indicating whether media is currently playing.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    ///     Gets the current playback position.
    /// </summary>
    TimeSpan CurrentPosition { get; }

    /// <summary>
    ///     Gets the duration of the currently loaded media.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    ///     Gets the current volume level, from 0.0 (silent) to 1.0 (maximum).
    /// </summary>
    double Volume { get; }

    /// <summary>
    ///     Gets a value indicating whether the player is currently muted.
    /// </summary>
    bool IsMuted { get; }

    #endregion

    #region Methods

    /// <summary>
    ///     Ensures the audio player is initialized and ready for playback.
    ///     This synchronous version is for use by sync callers. For async contexts,
    ///     prefer <see cref="EnsureInitializedAsync"/> to avoid blocking.
    /// </summary>
    void EnsureInitialized();

    /// <summary>
    ///     Asynchronously ensures the audio player is initialized and ready for playback.
    ///     This can be called from a background thread to pre-warm the audio engine
    ///     without blocking the UI thread during app startup. Multiple concurrent calls
    ///     will await the same initialization task.
    /// </summary>
    /// <returns>A task that completes when initialization is done.</returns>
    Task EnsureInitializedAsync();

    /// <summary>
    ///     Initializes the System Media Transport Controls for integration with the OS.
    /// </summary>
    void InitializeSmtc();

    /// <summary>
    ///     Updates the enabled state of the SMTC 'Next' and 'Previous' buttons.
    /// </summary>
    /// <param name="canNext">A value indicating whether the 'Next' button should be enabled.</param>
    /// <param name="canPrevious">A value indicating whether the 'Previous' button should be enabled.</param>
    void UpdateSmtcButtonStates(bool canNext, bool canPrevious);

    /// <summary>
    ///     Asynchronously loads a song for playback. The returned task completes when the media is successfully opened or has
    ///     failed to load.
    /// </summary>
    /// <param name="song">The song to load.</param>
    Task LoadAsync(Song song);

    /// <summary>
    ///     Starts or resumes playback of the loaded media.
    /// </summary>
    Task PlayAsync();

    /// <summary>
    ///     Pauses playback of the current media.
    /// </summary>
    Task PauseAsync();

    /// <summary>
    ///     Stops playback and unloads the current media.
    /// </summary>
    Task StopAsync();

    /// <summary>
    ///     Seeks to a specific position in the current media.
    /// </summary>
    /// <param name="position">The position to seek to.</param>
    Task SeekAsync(TimeSpan position);

    /// <summary>
    ///     Sets the player's volume.
    /// </summary>
    /// <param name="volume">The volume level, clamped between 0.0 and 1.0.</param>
    Task SetVolumeAsync(double volume);

    /// <summary>
    ///     Sets the player's mute state.
    /// </summary>
    /// <param name="isMuted">True to mute the player, false to unmute.</param>
    Task SetMuteAsync(bool isMuted);

    /// <summary>
    ///     Applies equalizer settings to the media player.
    /// </summary>
    /// <param name="settings">The equalizer settings to apply.</param>
    /// <returns>True if the equalizer was set successfully; otherwise, false.</returns>
    bool ApplyEqualizerSettings(EqualizerSettings settings);

    /// <summary>
    ///     Gets the descriptive information for each available equalizer band.
    /// </summary>
    /// <returns>A read-only list of band indices and their corresponding frequencies.</returns>
    IReadOnlyList<(uint Index, float Frequency)> GetEqualizerBands();

    /// <summary>
    ///     Sets the ReplayGain adjustment for volume normalization.
    /// </summary>
    /// <param name="gainDb">The gain adjustment in decibels. Use 0 for no adjustment.</param>
    Task SetReplayGainAsync(double gainDb);

    #endregion
}
