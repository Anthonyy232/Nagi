using System.ComponentModel.DataAnnotations;

namespace Nagi.Core.Models;

public class Artist
{
    public static string UnknownArtistName => string.Format(Resources.Strings.Format_Unknown, Resources.Strings.Label_Artist);
    public static string ArtistSeparator => Resources.Strings.ArtistSeparator;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(500)]
    public string Name { get; set; } = string.Format(Resources.Strings.Format_Unknown, Resources.Strings.Label_Artist);

    /// <summary>
    ///     Denormalized sort key for <see cref="Name"/> with leading articles ("the", "a", "an") stripped.
    ///     Populated before saving. Used by queries when the
    ///     <c>IgnoreLeadingArticlesOnSort</c> setting is enabled.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string SortName { get; set; } = string.Empty;

    [MaxLength(50000)]
    public string? Biography { get; set; }

    [MaxLength(2000)]
    public string? RemoteImageUrl { get; set; }

    [MaxLength(1000)]
    public string? LocalImageCachePath { get; set; }

    /// <summary>
    ///     The date and time when the artist's metadata was last successfully checked.
    ///     Null means the metadata still needs checking or a previous lookup failed.
    /// </summary>
    public DateTime? MetadataLastCheckedUtc { get; set; }

    /// <summary>
    ///     The MusicBrainz identifier for this artist.
    ///     Used to fetch metadata from services like Fanart.tv that require MBID.
    /// </summary>
    [MaxLength(100)]
    public string? MusicBrainzId { get; set; }

    public virtual ICollection<SongArtist> SongArtists { get; set; } = new List<SongArtist>();

    public virtual ICollection<AlbumArtist> AlbumArtists { get; set; } = new List<AlbumArtist>();

    public static string GetDisplayName(IEnumerable<string?>? artistNames)
    {
        var displayName = string.Join(ArtistSeparator, artistNames?.Where(n => !string.IsNullOrWhiteSpace(n)) ?? []);
        return displayName.Length == 0 ? UnknownArtistName : displayName;
    }

    public override string ToString()

    {
        return Name;
    }
}
