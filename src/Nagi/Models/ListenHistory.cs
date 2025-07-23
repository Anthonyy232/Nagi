using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Models;

/// <summary>
/// Records a single listening event for a song, used for tracking playback history.
/// </summary>
public class ListenHistory {
    /// <summary>
    /// The unique identifier for the listen event.
    /// </summary>
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// The foreign key of the song that was listened to.
    /// </summary>
    [Required]
    public Guid SongId { get; set; }

    /// <summary>
    /// The navigation property for the associated song.
    /// </summary>
    [ForeignKey("SongId")]
    public virtual Song Song { get; set; } = null!;

    /// <summary>
    /// The Coordinated Universal Time (UTC) when the song was listened to.
    /// </summary>
    public DateTime ListenTimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Indicates whether this listen has met the time requirements (e.g., >4 mins or 50% played)
    /// to be submitted to a scrobbling service. Defaults to false.
    /// </summary>
    public bool IsEligibleForScrobbling { get; set; }

    /// <summary>
    /// Indicates whether this listen event has been successfully submitted 
    /// to an external scrobbling service (e.g., Last.fm).
    /// </summary>
    public bool IsScrobbled { get; set; }
}