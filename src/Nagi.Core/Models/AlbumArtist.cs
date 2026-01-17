using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

/// <summary>
///     Join entity for the many-to-many relationship between Albums and Artists.
///     Includes an Order property to preserve the sequence of artists (e.g., "Artist A & Artist B").
/// </summary>
public class AlbumArtist
{
    /// <summary>
    ///     The foreign key for the album.
    /// </summary>
    public Guid AlbumId { get; set; }

    /// <summary>
    ///     Navigation property to the album.
    /// </summary>
    [ForeignKey("AlbumId")]
    public virtual Album Album { get; set; } = null!;

    /// <summary>
    ///     The foreign key for the artist.
    /// </summary>
    public Guid ArtistId { get; set; }

    /// <summary>
    ///     Navigation property to the artist.
    /// </summary>
    [ForeignKey("ArtistId")]
    public virtual Artist Artist { get; set; } = null!;

    /// <summary>
    ///     The sequence order of this artist for the album (0-indexed).
    /// </summary>
    public int Order { get; set; }
}
