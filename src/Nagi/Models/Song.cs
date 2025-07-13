using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Models;

/// <summary>
/// Represents a single song track in the music library.
/// </summary>
public class Song {
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Title { get; set; } = "Unknown Title";

    public Guid? AlbumId { get; set; }

    [ForeignKey("AlbumId")]
    public virtual Album? Album { get; set; }

    public Guid? ArtistId { get; set; }

    [ForeignKey("ArtistId")]
    public virtual Artist? Artist { get; set; }

    /// <summary>
    /// The composer of the song.
    /// </summary>
    public string? Composer { get; set; }

    [Required]
    public Guid FolderId { get; set; }

    [ForeignKey("FolderId")]
    public virtual Folder Folder { get; set; } = null!;

    public TimeSpan Duration { get; set; } = TimeSpan.Zero;

    public string? AlbumArtUriFromTrack { get; set; }

    [Required]
    public string FilePath { get; set; } = string.Empty;

    public int? Year { get; set; }
    public int? TrackNumber { get; set; }
    public int? TrackCount { get; set; }
    public int? DiscNumber { get; set; }
    public int? DiscCount { get; set; }
    public int? SampleRate { get; set; }
    public int? Bitrate { get; set; }
    public int? Channels { get; set; }
    public DateTime? DateAddedToLibrary { get; set; } = DateTime.UtcNow;
    public DateTime? FileCreatedDate { get; set; }
    public DateTime? FileModifiedDate { get; set; }
    public string? LightSwatchId { get; set; }
    public string? DarkSwatchId { get; set; }

    /// <summary>
    /// A user-assigned rating, typically from 1 to 5.
    /// </summary>
    public int? Rating { get; set; }

    /// <summary>
    /// Indicates if the user has marked this song as a favorite.
    /// </summary>
    public bool IsLoved { get; set; }

    /// <summary>
    /// The total number of times the song has been played.
    /// </summary>
    public int PlayCount { get; set; }

    /// <summary>
    /// The total number of times the song has been skipped.
    /// </summary>
    public int SkipCount { get; set; }

    /// <summary>
    /// The date and time the song was last played.
    /// </summary>
    public DateTime? LastPlayedDate { get; set; }

    /// <summary>
    /// The lyrics of the song.
    /// </summary>
    public string? Lyrics { get; set; }

    /// <summary>
    /// The beats per minute of the track.
    /// </summary>
    public double? Bpm { get; set; }

    [NotMapped]
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(AlbumArtUriFromTrack);

    // Navigation properties
    public virtual ICollection<Genre> Genres { get; set; } = new List<Genre>();
    public virtual ICollection<PlaylistSong> PlaylistSongs { get; set; } = new List<PlaylistSong>();
    public virtual ICollection<ListenHistory> ListenHistory { get; set; } = new List<ListenHistory>();

    public override string ToString() {
        return $"{Title} by {Artist?.Name ?? "Unknown Artist"}";
    }

    public override bool Equals(object? obj) {
        if (obj is not Song other) return false;
        return Id.Equals(other.Id);
    }

    public override int GetHashCode() {
        return Id.GetHashCode();
    }
}