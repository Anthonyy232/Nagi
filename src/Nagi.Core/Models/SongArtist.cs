using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

/// <summary>
///     Join entity for the many-to-many relationship between Songs and Artists.
///     Includes an Order property to preserve the sequence of artists (e.g., "Artist A & Artist B").
/// </summary>
public class SongArtist
{
    public Guid SongId { get; set; }

    [ForeignKey("SongId")]
    public virtual Song Song { get; set; } = null!;

    public Guid ArtistId { get; set; }

    [ForeignKey("ArtistId")]
    public virtual Artist Artist { get; set; } = null!;

    /// <summary>
    ///     The sequence order of this artist for the song (0-indexed).
    /// </summary>
    public int Order { get; set; }
}
