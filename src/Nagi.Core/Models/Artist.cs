using System.ComponentModel.DataAnnotations;

namespace Nagi.Core.Models;

/// <summary>
///     Represents a musical artist or band.
/// </summary>
public class Artist
{
    /// <summary>
    ///     The unique identifier for the artist.
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     The name of the artist.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Name { get; set; } = "Unknown Artist";

    /// <summary>
    ///     A biography of the artist, typically fetched from an external service.
    /// </summary>
    [MaxLength(50000)]
    public string? Biography { get; set; }

    /// <summary>
    ///     The remote URL of the artist's image.
    /// </summary>
    [MaxLength(2000)]
    public string? RemoteImageUrl { get; set; }

    /// <summary>
    ///     The local file path to the cached artist image.
    /// </summary>
    [MaxLength(1000)]
    public string? LocalImageCachePath { get; set; }

    /// <summary>
    ///     The date and time when the artist's metadata was last successfully checked.
    ///     Null indicates never checked and received a proper empty response
    /// </summary>
    public DateTime? MetadataLastCheckedUtc { get; set; }

    /// <summary>
    ///     A collection of songs by this artist.
    /// </summary>
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    /// <summary>
    ///     A collection of albums by this artist.
    /// </summary>
    public virtual ICollection<Album> Albums { get; set; } = new List<Album>();

    public override string ToString()
    {
        return Name;
    }
}