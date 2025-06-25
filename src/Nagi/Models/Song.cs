using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Models;

/// <summary>
///     Represents a single song track in the music library.
/// </summary>
public class Song
{
    /// <summary>
    ///     The unique identifier for the song.
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     The title of the song.
    /// </summary>
    [Required]
    public string Title { get; set; } = "Unknown Title";

    /// <summary>
    ///     The foreign key for the song's album, if any.
    /// </summary>
    public Guid? AlbumId { get; set; }

    /// <summary>
    ///     The navigation property to the song's album.
    /// </summary>
    [ForeignKey("AlbumId")]
    public virtual Album? Album { get; set; }

    /// <summary>
    ///     The foreign key for the song's primary artist.
    /// </summary>
    public Guid? ArtistId { get; set; }

    /// <summary>
    ///     The navigation property to the song's primary artist.
    /// </summary>
    [ForeignKey("ArtistId")]
    public virtual Artist? Artist { get; set; }

    /// <summary>
    ///     The foreign key for the folder containing this song's file.
    /// </summary>
    public Guid FolderId { get; set; }

    /// <summary>
    ///     The navigation property to the folder containing this song's file.
    /// </summary>
    [ForeignKey("FolderId")]
    public virtual Folder Folder { get; set; } = null!;

    /// <summary>
    ///     The duration of the song.
    /// </summary>
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;

    /// <summary>
    ///     The URI or path to the cover art embedded in the song's file.
    /// </summary>
    public string? AlbumArtUriFromTrack { get; set; }

    /// <summary>
    ///     The full file system path to the audio file.
    /// </summary>
    [Required]
    public string? FilePath { get; set; }

    /// <summary>
    ///     A list of genres associated with the song.
    /// </summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>
    ///     The release year of the track.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    ///     The track number on its album disc.
    /// </summary>
    public int? TrackNumber { get; set; }

    /// <summary>
    ///     The disc number if the album is a multi-disc set.
    /// </summary>
    public int? DiscNumber { get; set; }

    /// <summary>
    ///     The audio sample rate in Hertz (Hz).
    /// </summary>
    public int? SampleRate { get; set; }

    /// <summary>
    ///     The audio bitrate in kilobits per second (kbps).
    /// </summary>
    public int? Bitrate { get; set; }

    /// <summary>
    ///     The number of audio channels.
    /// </summary>
    public int? Channels { get; set; }

    /// <summary>
    ///     The date and time the song was added to the library.
    /// </summary>
    public DateTime? DateAddedToLibrary { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     The creation date of the audio file.
    /// </summary>
    public DateTime? FileCreatedDate { get; set; }

    /// <summary>
    ///     The last modification date of the audio file.
    /// </summary>
    public DateTime? FileModifiedDate { get; set; }

    /// <summary>
    ///     An identifier for a color swatch from the cover art, suitable for light UI themes.
    /// </summary>
    public string? LightSwatchId { get; set; }

    /// <summary>
    ///     An identifier for a color swatch from the cover art, suitable for dark UI themes.
    /// </summary>
    public string? DarkSwatchId { get; set; }

    /// <summary>
    ///     A collection of join entities linking this song to playlists.
    /// </summary>
    public virtual ICollection<PlaylistSong> PlaylistSongs { get; set; } = new List<PlaylistSong>();

    /// <summary>
    ///     Indicates whether song-specific artwork is available.
    /// </summary>
    [NotMapped]
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(AlbumArtUriFromTrack);

    public override string ToString()
    {
        return $"{Title} by {Artist?.Name ?? "Unknown Artist"}";
    }

    public override bool Equals(object? obj)
    {
        if (obj is not Song other) return false;
        return Id.Equals(other.Id);
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }
}