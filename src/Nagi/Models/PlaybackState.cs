using System;
using System.Collections.Generic;

namespace Nagi.Models;

/// <summary>
///     Represents the state of the media playback engine.
/// </summary>
public class PlaybackState
{
    /// <summary>
    ///     The ID of the currently playing or paused track.
    /// </summary>
    public Guid? CurrentTrackId { get; set; }

    /// <summary>
    ///     The playback position of the current track in seconds.
    /// </summary>
    public double CurrentPositionSeconds { get; set; }

    /// <summary>
    ///     The list of track IDs in the original, unshuffled playback queue.
    /// </summary>
    public List<Guid> PlaybackQueueTrackIds { get; set; } = new();

    /// <summary>
    ///     The index of the current track within the original playback queue.
    /// </summary>
    public int CurrentPlaybackQueueIndex { get; set; }

    /// <summary>
    ///     The list of track IDs in the shuffled playback queue.
    /// </summary>
    public List<Guid> ShuffledQueueTrackIds { get; set; } = new();

    /// <summary>
    ///     The index of the current track within the shuffled playback queue.
    /// </summary>
    public int CurrentShuffledQueueIndex { get; set; }
}