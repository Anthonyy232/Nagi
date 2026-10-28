using System.ComponentModel.DataAnnotations;
using Nagi.Core.Helpers;

namespace Nagi.Core.Models;

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

    /// <summary>
    ///     When online sources were last asked for a cover. Null means the album still needs a lookup
    ///     or the previous one failed.
    /// </summary>
    public DateTime? CoverArtLastCheckedUtc { get; set; }

    public virtual ICollection<AlbumArtist> AlbumArtists { get; set; } = new List<AlbumArtist>();
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    /// <summary>
    ///     The names of all associated artists joined by " & ".
    ///     This is a denormalized field for efficient display and searching.
    /// </summary>
    [MaxLength(Artist.MaxCreditLength)]
    public string ArtistName { get; set; } = Artist.UnknownArtistName;

    /// <summary>
    ///     The name of the primary artist (the one with the lowest order).
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
        SortTitle = SortKeyHelper.Normalize(Title);

        // Fast path: a single artist is the common case during scans, so skip the LINQ and joining.
        if (AlbumArtists.Count == 1 && AlbumArtists.First().Artist?.Name is { Length: > 0 } name)
        {
            (ArtistName, PrimaryArtistName, PrimaryArtistSortName) = (name, name, SortKeyHelper.Normalize(name));
            return;
        }

        (ArtistName, PrimaryArtistName, PrimaryArtistSortName) = Artist.GetCreditNames(AlbumArtists.OrderBy(aa => aa.Order).Select(aa => aa.Artist?.Name));
    }

    public override string ToString()
    {
        return $"{Title} — {PrimaryArtistName}";
    }
}
