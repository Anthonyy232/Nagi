using System;

namespace Nagi.Presentation.Models;

/// <summary>
///     The columns a song list can show. Saved in settings as a number, so existing values must not change.
/// </summary>
[Flags]
public enum SongListColumns
{
    None = 0,
    TrackNumber = 1 << 0,
    Artist = 1 << 1,
    Album = 1 << 2,
    Year = 1 << 3,
    Duration = 1 << 4,
    Bpm = 1 << 5,
    Bitrate = 1 << 6,
    PlayCount = 1 << 7,
    LastPlayed = 1 << 8,
    DateAdded = 1 << 9,
    FileCreatedDate = 1 << 10,
    Title = 1 << 11
}
