namespace Nagi.Core.Playback;

/// <summary>
///     The queue saved between sessions. Shuffle and repeat are settings of their own, since they outlast the queue.
/// </summary>
public class PlaybackState
{
    public List<Guid> PlaybackQueueTrackIds { get; set; } = [];

    /// <summary>The play order while shuffling, or empty.</summary>
    public List<Guid> ShuffledQueueTrackIds { get; set; } = [];

    public ShuffleMode ShuffleMode { get; set; }

    /// <summary>The song at the queue's position: the one loaded, or the one playback resumes with while stopped.</summary>
    public Guid? PositionId { get; set; }

    /// <summary>Whether the song at <see cref="PositionId" /> was loaded, so it is loaded again, paused.</summary>
    public bool IsLoaded { get; set; }
}
