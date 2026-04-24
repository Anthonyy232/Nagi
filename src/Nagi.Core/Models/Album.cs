using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nagi.Core.Helpers;

namespace Nagi.Core.Models;

/// <summary>
///     Represents a music album, which is a collection of songs.
/// </summary>
public class Album
{
    public static string UnknownAlbumName => string.Format(Resources.Strings.Format_Unknown, Resources.Strings.Label_Album);
    [Key] public Guid Id { get; set; } = Guid.NewGuid();


    [Required][MaxLength(500)] public string Title { get; set; } = string.Format(Resources.Strings.Format_Unknown, Resources.Strings.Label_Album);

    /// <summary>
    ///     Denormalized sort key for <see cref="Title"/> with leading articles ("the", "a", "an") stripped.
    ///     Populated by <see cref="SyncDenormalizedFields"/>.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string SortTitle { get; set; } = string.Empty;

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
    ///     Denormalized sort key for <see cref="PrimaryArtistName"/> with leading articles stripped.
    ///     Populated by <see cref="SyncDenormalizedFields"/>.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string PrimaryArtistSortName { get; set; } = string.Empty;


    /// <summary>
    ///     Updates the denormalized <see cref="ArtistName" />, <see cref="PrimaryArtistName" />,
    ///     <see cref="SortTitle"/> and <see cref="PrimaryArtistSortName"/> fields based on the
    ///     current <see cref="Title"/> and <see cref="AlbumArtists" /> collection.
    /// </summary>
    public void SyncDenormalizedFields()
    {
        const int MaxArtistNameLength = 2000;

        SortTitle = SortKeyHelper.Normalize(Title);

        // Fast path: single artist is the common case — skip LINQ, OrderBy, and GetDisplayName allocations.
        if (AlbumArtists.Count == 1)
        {
            var name = AlbumArtists.First().Artist?.Name;
            if (!string.IsNullOrEmpty(name))
            {
                ArtistName = name;
                PrimaryArtistName = name;
                PrimaryArtistSortName = SortKeyHelper.Normalize(name);
                return;
            }
        }

        var artists = AlbumArtists.OrderBy(aa => aa.Order).Select(aa => aa.Artist?.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (artists.Count == 0)
        {
            ArtistName = Artist.UnknownArtistName;
            PrimaryArtistName = Artist.UnknownArtistName;
            PrimaryArtistSortName = SortKeyHelper.Normalize(Artist.UnknownArtistName);
        }
        else
        {
            var displayName = Artist.GetDisplayName(artists);
            // Truncate to MaxLength to prevent database overflow with many collaborators
            ArtistName = displayName.Length > MaxArtistNameLength
                ? displayName[..(MaxArtistNameLength - 3)] + "..."
                : displayName;
            PrimaryArtistName = artists[0] ?? Artist.UnknownArtistName;
            PrimaryArtistSortName = SortKeyHelper.Normalize(PrimaryArtistName);
        }

    }

    public override string ToString()
    {
        return $"{Title} by {PrimaryArtistName}";
    }
}
