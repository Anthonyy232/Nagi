using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

public class ListenHistory
{
    [Key]
    public long Id { get; set; }

    [Required]
    public Guid SongId { get; set; }

    [ForeignKey("SongId")]
    public virtual Song Song { get; set; } = null!;

    public DateTime ListenTimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Indicates whether this listen has met the time requirements (e.g., >4 mins or 50% played)
    ///     to be submitted to a scrobbling service.
    /// </summary>
    public bool IsEligibleForScrobbling { get; set; }

    public long ListenDurationTicks { get; set; }

    public PlaybackEndReason EndReason { get; set; }

    public PlaybackContextType ContextType { get; set; }

    public Guid? ContextId { get; set; }

    /// <summary>
    ///     Indicates whether this listen event has been successfully submitted
    ///     to an external scrobbling service (e.g., Last.fm).
    /// </summary>
    public bool IsScrobbled { get; set; }

    public bool IsSubmittedToListenBrainz { get; set; }
}
