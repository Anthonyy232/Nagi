using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

/// <summary>
/// Represents a music album, which is a collection of songs.
/// </summary>
public class Album {
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Title { get; set; } = "Unknown Album";

    public int? Year { get; set; }

    public string? CoverArtUri { get; set; }

    /// <summary>
    /// The foreign key for the album's primary artist.
    /// </summary>
    [Required]
    public Guid ArtistId { get; set; }

    /// <summary>
    /// The navigation property to the album's primary artist.
    /// </summary>
    [ForeignKey("ArtistId")]
    public virtual Artist Artist { get; set; } = null!;

    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    [NotMapped]
    public string PrimaryArtistNameForDisplay => Artist?.Name ?? "Unknown Artist";

    public override string ToString() {
        return $"{Title} by {PrimaryArtistNameForDisplay}";
    }
}