using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;

namespace Nagi.Models;

/// <summary>
///     Represents a music album, which is a collection of songs by an artist.
/// </summary>
public class Album {
    /// <summary>
    ///     The unique identifier for the album.
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     The title of the album.
    /// </summary>
    [Required]
    public string Title { get; set; } = "Unknown Album";

    /// <summary>
    ///     The release year of the album.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    ///     A direct path or URI to the album's cover art.
    ///     This is persisted in the database and is typically sourced from the first available track.
    /// </summary>
    public string? CoverArtUri { get; set; }

    /// <summary>
    ///     The foreign key for the album's primary artist.
    /// </summary>
    public Guid? ArtistId { get; set; }

    /// <summary>
    ///     The navigation property to the album's primary artist.
    /// </summary>
    [ForeignKey("ArtistId")]
    public virtual Artist? Artist { get; set; }

    /// <summary>
    ///     The collection of songs included in this album.
    /// </summary>
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    /// <summary>
    ///     A display-friendly name of the primary artist.
    /// </summary>
    [NotMapped]
    public string PrimaryArtistNameForDisplay => Artist?.Name ?? "Unknown Artist";

    public override string ToString() {
        return $"{Title} by {PrimaryArtistNameForDisplay}";
    }
}