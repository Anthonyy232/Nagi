using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Library;

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

    /// <summary>
    ///     The song's sort tag for this artist as a sort key, or null when it has none.
    /// </summary>
    [MaxLength(500)]
    public string? SortName { get; set; }
}
