using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

/// <summary>
///     Represents a music album, which is a collection of songs.
/// </summary>
public class Album
{
    public const string UnknownAlbumName = "Unknown Album";
    [Key] public Guid Id { get; set; } = Guid.NewGuid();


    [Required] [MaxLength(500)] public string Title { get; set; } = "Unknown Album";

    public int? Year { get; set; }

    [MaxLength(2000)] public string? CoverArtUri { get; set; }

    // Simplified multi-artist relationship. ArtistId and Artist are moved to AlbumArtists.

    public virtual ICollection<AlbumArtist> AlbumArtists { get; set; } = new List<AlbumArtist>();
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    /// <summary>
    ///     Gets or sets the names of all associated artists joined by " & ".
    ///     This is a denormalized field for efficient display and searching.
    /// </summary>
    [MaxLength(2000)]
    public string ArtistName { get; set; } = Artist.UnknownArtistName;


    /// <summary>
    ///     Gets or sets the name of the primary artist (the one with the lowest order).
    ///     This is a denormalized field for efficient sorting.
    /// </summary>
    [MaxLength(500)]
    public string PrimaryArtistName { get; set; } = Artist.UnknownArtistName;


    /// <summary>
    ///     Updates the denormalized <see cref="ArtistName" /> and <see cref="PrimaryArtistName" /> 
    ///     fields based on the current <see cref="AlbumArtists" /> collection.
    /// </summary>
    public void SyncDenormalizedFields()
    {
        const int MaxArtistNameLength = 2000;
        
        var artists = AlbumArtists.OrderBy(aa => aa.Order).Select(aa => aa.Artist?.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (artists.Count == 0)
        {
            ArtistName = Artist.UnknownArtistName;
            PrimaryArtistName = Artist.UnknownArtistName;
        }
        else
        {
            var displayName = Artist.GetDisplayName(artists);
            // Truncate to MaxLength to prevent database overflow with many collaborators
            ArtistName = displayName.Length > MaxArtistNameLength 
                ? displayName[..(MaxArtistNameLength - 3)] + "..." 
                : displayName;
            PrimaryArtistName = artists[0] ?? Artist.UnknownArtistName;
        }

    }

    public override string ToString()
    {
        return $"{Title} by {PrimaryArtistName}";
    }
}