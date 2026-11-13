using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Library;

public class AlbumArtist
{
    public Guid AlbumId { get; set; }

    [ForeignKey("AlbumId")]
    public virtual Album Album { get; set; } = null!;

    public Guid ArtistId { get; set; }

    [ForeignKey("ArtistId")]
    public virtual Artist Artist { get; set; } = null!;

    /// <summary>
    ///     The sequence order of this artist for the album (0-indexed).
    /// </summary>
    public int Order { get; set; }
}
