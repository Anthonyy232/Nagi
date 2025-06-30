using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Nagi.Models;

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
    public string Name { get; set; } = "Unknown Artist";

    /// <summary>
    ///     A biography of the artist, typically fetched from an external service.
    /// </summary>
    public string? Biography { get; set; }

    /// <summary>
    ///     The remote URL of the artist's image.
    /// </summary>
    public string? RemoteImageUrl { get; set; }

    /// <summary>
    ///     The local file path to the cached artist image.
    /// </summary>
    public string? LocalImageCachePath { get; set; }

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