namespace Nagi.Core.Models;

/// <summary>
///     A data transfer object representing metadata extracted from a single audio file.
/// </summary>
public class SongFileMetadata
{
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? Album { get; set; } = string.Empty;
    public string? AlbumArtist { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string? CoverArtUri { get; set; }
    public string? LightSwatchId { get; set; }
    public string? DarkSwatchId { get; set; }
    public int? Year { get; set; }
    public List<string> Genres { get; set; } = new();
    public int? TrackNumber { get; set; }
    public int? TrackCount { get; set; }
    public int? DiscNumber { get; set; }
    public int? DiscCount { get; set; }
    public int? SampleRate { get; set; }
    public int? Bitrate { get; set; }
    public int? Channels { get; set; }
    public DateTime? FileCreatedDate { get; set; }
    public DateTime? FileModifiedDate { get; set; }
    public string? Lyrics { get; set; }
    public string? LrcFilePath { get; set; }
    public double? Bpm { get; set; }
    public string? Composer { get; set; }
    public bool ExtractionFailed { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Grouping { get; set; }
    public string? Copyright { get; set; }
    public string? Comment { get; set; }
    public string? Conductor { get; set; }
    public string? MusicBrainzTrackId { get; set; }
    public string? MusicBrainzReleaseId { get; set; }
}