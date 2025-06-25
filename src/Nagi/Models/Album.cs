using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;

namespace Nagi.Models;

/// <summary>
///     Represents a music album, which is a collection of songs by an artist.
/// </summary>
public class Album
{
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

    /// <summary>
    ///     The cover art URI derived from the first available artwork among its songs.
    ///     This property requires the Songs collection to be loaded.
    /// </summary>
    [NotMapped]
    public string? CalculatedCoverArtUri => Songs
        .OrderBy(s => s.DiscNumber ?? 1)
        .ThenBy(s => s.TrackNumber ?? 1)
        .ThenBy(s => s.Title)
        .FirstOrDefault(s => !string.IsNullOrEmpty(s.AlbumArtUriFromTrack))
        ?.AlbumArtUriFromTrack;

    public override string ToString()
    {
        return $"{Title} by {PrimaryArtistNameForDisplay}";
    }
}