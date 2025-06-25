// Nagi/Models/SongFileMetadata.cs

using System;
using System.Collections.Generic;

namespace Nagi.Models;

public class SongFileMetadata
{
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = "";
    public string? Album { get; set; } = "";
    public string? AlbumArtist { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public string? CoverArtUri { get; set; }
    public string? LightSwatchId { get; set; }
    public string? DarkSwatchId { get; set; }
    public int? Year { get; set; }
    public List<string> Genres { get; set; } = new();
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public int? SampleRate { get; set; }
    public int? Bitrate { get; set; }
    public int? Channels { get; set; }
    public DateTime? FileCreatedDate { get; set; }
    public DateTime? FileModifiedDate { get; set; }
    public bool ExtractionFailed { get; set; } = false;
    public string? ErrorMessage { get; set; }
}