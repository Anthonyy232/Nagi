namespace Nagi.Core.Models;

public enum PlaybackEndReason
{
    Finished = 0,

    /// <summary>
    ///     The user manually skipped to another track or explicitly stopped playback.
    /// </summary>
    Skipped = 1,

    /// <summary>
    ///     The track was paused and never resumed before being cleared from the queue or the app closing.
    /// </summary>
    PausedAndAbandoned = 2,

    Error = 3
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
