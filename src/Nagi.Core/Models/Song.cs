using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

/// <summary>
///     Represents a single song track in the music library.
/// </summary>
public class Song
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    [Required] [MaxLength(500)] public string Title { get; set; } = string.Format(Resources.Strings.Format_Unknown, Resources.Strings.Label_Title);

    public Guid? AlbumId { get; set; }

    [ForeignKey("AlbumId")] public virtual Album? Album { get; set; }

    // Simplified multi-artist relationship. ArtistId and Artist are moved to SongArtists.
    // Migration will attempt to preserve existing data.

    [MaxLength(200)] public string? Composer { get; set; }

    [Required] public Guid FolderId { get; set; }

    [ForeignKey("FolderId")] public virtual Folder Folder { get; set; } = null!;

    /// <summary>
    ///     The duration of the song stored as ticks for database compatibility with SQLite.
    /// </summary>
    public long DurationTicks { get; set; }

    /// <summary>
    ///     The duration of the song as a TimeSpan (computed from DurationTicks).
    /// </summary>
    [NotMapped]
    public TimeSpan Duration
    {
        get => TimeSpan.FromTicks(DurationTicks);
        set => DurationTicks = value.Ticks;
    }

    [MaxLength(2000)] public string? AlbumArtUriFromTrack { get; set; }

    [Required] [MaxLength(1000)] public string FilePath { get; set; } = string.Empty;

    /// <summary>
    ///     The directory path where the song file is located. This is used for efficient
    ///     folder hierarchy navigation and querying songs by directory.
    /// </summary>
    [Required]
    [MaxLength(1000)]
    public string DirectoryPath { get; set; } = string.Empty;

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
    [MaxLength(50)] public string? LightSwatchId { get; set; }
    [MaxLength(50)] public string? DarkSwatchId { get; set; }

    /// <summary>
    ///     A user-assigned rating, typically from 1 to 5.
    /// </summary>
    public int? Rating { get; set; }

    /// <summary>
    ///     Indicates if the user has marked this song as a favorite.
    /// </summary>
    public bool IsLoved { get; set; }

    /// <summary>
    ///     The total number of times the song has been played.
    /// </summary>
    public int PlayCount { get; set; }

    /// <summary>
    ///     The total number of times the song has been skipped.
    /// </summary>
    public int SkipCount { get; set; }

    /// <summary>
    ///     The date and time the song was last played.
    /// </summary>
    public DateTime? LastPlayedDate { get; set; }

    /// <summary>
    ///     The lyrics of the song.
    /// </summary>
    [MaxLength(50000)]
    public string? Lyrics { get; set; }

    /// <summary>
    ///     The file path to the synchronized .lrc lyrics file associated with this song.
    /// </summary>
    [MaxLength(1000)]
    public string? LrcFilePath { get; set; }

    /// <summary>
    ///     The date and time when online lyrics were last searched.
    ///     Null indicates never checked. If set, online fetch will be skipped.
    /// </summary>
    public DateTime? LyricsLastCheckedUtc { get; set; }

    /// <summary>
    ///     The beats per minute of the track.
    /// </summary>
    public double? Bpm { get; set; }

    /// <summary>
    ///     The ReplayGain track gain value in dB. Used for volume normalization.
    /// </summary>
    public double? ReplayGainTrackGain { get; set; }

    /// <summary>
    ///     The ReplayGain track peak value (0.0 to 1.0). Used to prevent clipping.
    /// </summary>
    public double? ReplayGainTrackPeak { get; set; }

    /// <summary>
    ///     Transient flag indicating if ReplayGain data has been checked for this song instance.
    ///     Used to prevent repeated database lookups during playback when no data exists.
    /// </summary>
    [NotMapped]
    public bool ReplayGainCheckPerformed { get; set; }

    /// <summary>
    ///     A custom grouping category for the song.
    /// </summary>
    [MaxLength(200)]
    public string? Grouping { get; set; }

    /// <summary>
    ///     Copyright information for the track.
    /// </summary>
    [MaxLength(1000)]
    public string? Copyright { get; set; }

    /// <summary>
    ///     A general-purpose comment field.
    /// </summary>
    [MaxLength(1000)]
    public string? Comment { get; set; }

    /// <summary>
    ///     The conductor of the orchestra, if applicable.
    /// </summary>
    [MaxLength(200)]
    public string? Conductor { get; set; }

    /// <summary>
    ///     The unique identifier for the track from the MusicBrainz database.
    /// </summary>
    [MaxLength(100)]
    public string? MusicBrainzTrackId { get; set; }

    /// <summary>
    ///     The unique identifier for the release (album) from the MusicBrainz database.
    /// </summary>
    [MaxLength(100)]
    public string? MusicBrainzReleaseId { get; set; }

    [NotMapped] public double Order { get; set; }
    [NotMapped] public bool IsArtworkAvailable => !string.IsNullOrEmpty(AlbumArtUriFromTrack);

    [NotMapped] public bool HasTimedLyrics => !string.IsNullOrEmpty(LrcFilePath);

    [NotMapped]
    public string TrackDisplay
    {
        get
        {
            if (DiscNumber.HasValue && DiscNumber.Value > 0)
            {
                return TrackNumber.HasValue
                    ? $"{DiscNumber}-{TrackNumber}"
                    : $"{DiscNumber}";
            }
            return TrackNumber?.ToString() ?? string.Empty;
        }
    }

    // Navigation properties
    public virtual ICollection<SongArtist> SongArtists { get; set; } = new List<SongArtist>();
    public virtual ICollection<Genre> Genres { get; set; } = new List<Genre>();
    public virtual ICollection<PlaylistSong> PlaylistSongs { get; set; } = new List<PlaylistSong>();
    public virtual ICollection<ListenHistory> ListenHistory { get; set; } = new List<ListenHistory>();

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
    ///     fields based on the current <see cref="SongArtists" /> collection.
    /// </summary>
    public void SyncDenormalizedFields()
    {
        const int MaxArtistNameLength = 2000;
        
        var artists = SongArtists.OrderBy(sa => sa.Order).Select(sa => sa.Artist?.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
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
        return $"{Title} by {ArtistName}";
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