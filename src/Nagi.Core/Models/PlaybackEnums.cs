namespace Nagi.Core.Models;

/// <summary>
///     Specifies the reason why a track finished playing.
/// </summary>
public enum PlaybackEndReason
{
    /// <summary>
    ///     The track played naturally to its end.
    /// </summary>
    Finished = 0,

    /// <summary>
    ///     The user manually skipped to another track or explicitly stopped playback.
    /// </summary>
    Skipped = 1,

    /// <summary>
    ///     The track was paused and never resumed before being cleared from the queue or the app closing.
    /// </summary>
    PausedAndAbandoned = 2,

    /// <summary>
    ///     Playback was interrupted by a technical error.
    /// </summary>
    Error = 3
}

/// <summary>
///     Specifies the UI context from which a song was played.
/// </summary>
public enum PlaybackContextType
{
    /// <summary>
    ///     Played from an album view.
    /// </summary>
    Album = 0,

    /// <summary>
    ///     Played from an artist's track list.
    /// </summary>
    Artist = 1,

    /// <summary>
    ///     Played from a manual user playlist.
    /// </summary>
    Playlist = 2,

    /// <summary>
    ///     Played from a rule-based smart playlist.
    /// </summary>
    SmartPlaylist = 3,

    /// <summary>
    ///     Played from a folder navigation view.
    /// </summary>
    Folder = 4,

    /// <summary>
    ///     Played from a genre list.
    /// </summary>
    Genre = 5,

    /// <summary>
    ///     Played from search results.
    /// </summary>
    Search = 6,

    /// <summary>
    ///     Played directly from the ad-hoc playback queue.
    /// </summary>
    Queue = 7,

    /// <summary>
    ///     Played as a single file without library context.
    /// </summary>
    Transient = 8
}

/// <summary>
///     Represents the metadata context for a listen event.
/// </summary>
public record PlaybackContext(PlaybackContextType Type, Guid? ContextId);
