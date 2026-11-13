using Nagi.Core.Library;

namespace Nagi.Core.Playback;

/// <summary>
///     The operating system's media overlay and media keys, such as the System Media Transport Controls on
///     Windows. Call it from the UI thread; its events are raised there too.
/// </summary>
public interface ISystemMediaControls
{
    event Action? PlayPressed;
    event Action? PausePressed;
    event Action? NextPressed;
    event Action? PreviousPressed;

    /// <summary>
    ///     The requested position is not checked against the track length.
    /// </summary>
    event Action<TimeSpan>? SeekRequested;

    void SetNavigationEnabled(bool canGoNext, bool canGoPrevious);

    void SetPlaybackStatus(SystemMediaPlaybackStatus status);

    /// <summary>
    ///     The OS advances the position itself while playing, so call this when the track, its length or the
    ///     playback state changes and after a seek, not on every tick.
    /// </summary>
    void SetTimeline(TimeSpan position, TimeSpan duration);

    /// <summary>
    ///     Shows <paramref name="song" /> as now playing. A later call wins over one still loading its artwork.
    /// </summary>
    Task SetNowPlayingAsync(Song song);

    /// <summary>
    ///     Clears the now-playing display and shows playback as stopped.
    /// </summary>
    void Clear();
}
