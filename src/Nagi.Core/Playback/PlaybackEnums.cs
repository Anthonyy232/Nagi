namespace Nagi.Core.Playback;

public enum PlaybackEndReason
{
    Finished = 0,

    /// <summary>
    ///     Another track was chosen before this one ended.
    /// </summary>
    Skipped = 1,

    /// <summary>
    ///     Playback stopped or the app closed before the track ended.
    /// </summary>
    PausedAndAbandoned = 2
}

public enum PlaybackContextType
{
    Album = 0,

    Artist = 1,

    Playlist = 2,

    SmartPlaylist = 3,

    Folder = 4,

    Genre = 5,

    Search = 6,

    /// <summary>
    ///     Tracks played from the main library or ad-hoc queue.
    /// </summary>
    Library = 7,

    /// <summary>
    ///     Played as a single file without library context.
    /// </summary>
    Transient = 8
}

public enum PlaybackStopMode
{
    Off,
    AfterTrack,
    EndOfQueue,
    Timer
}

public record PlaybackContext(PlaybackContextType Type, Guid? ContextId);

/// <summary>
///     The playback state shown by the operating system's media controls.
/// </summary>
public enum SystemMediaPlaybackStatus
{
    Closed,
    Changing,
    Stopped,
    Playing,
    Paused
}
