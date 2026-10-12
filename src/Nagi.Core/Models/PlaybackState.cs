namespace Nagi.Core.Models;

public class PlaybackState
{
    public Guid? CurrentTrackId { get; set; }

    public ShuffleMode ShuffleMode { get; set; }

    /// <summary>
    ///     The list of track IDs in the original, unshuffled playback queue.
    /// </summary>
    public List<Guid> PlaybackQueueTrackIds { get; set; } = new();

    public int CurrentPlaybackQueueIndex { get; set; }

    public List<Guid> ShuffledQueueTrackIds { get; set; } = new();

    public int CurrentShuffledQueueIndex { get; set; }
}
