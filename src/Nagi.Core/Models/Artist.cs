using System.ComponentModel.DataAnnotations;
using Nagi.Core.Helpers;

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

    /// <summary>The column length for a song's or album's joined artist names.</summary>
    public const int MaxCreditLength = 2000;

    public static string GetDisplayName(IEnumerable<string?>? artistNames)
    {
        var displayName = string.Join(ArtistSeparator, artistNames?.Where(n => !string.IsNullOrWhiteSpace(n)) ?? []);
        return displayName.Length == 0 ? UnknownArtistName : displayName;
    }

    /// <summary>
    ///     The joined display name, primary name and primary sort key for artists listed in credit order,
    ///     falling back to the unknown artist when none has a name.
    /// </summary>
    internal static (string Display, string Primary, string PrimarySortKey) GetCreditNames(IEnumerable<string?> namesInOrder)
    {
        var names = namesInOrder.Where(name => !string.IsNullOrEmpty(name)).Cast<string>().ToList();
        if (names.Count == 0) return (UnknownArtistName, UnknownArtistName, SortKeyHelper.Normalize(UnknownArtistName));

        var display = GetDisplayName(names);
        if (display.Length > MaxCreditLength) display = display[..(MaxCreditLength - 3)] + "...";
        return (display, names[0], SortKeyHelper.Normalize(names[0]));
    }

    public override string ToString()

    {
        return Name;
    }
}
