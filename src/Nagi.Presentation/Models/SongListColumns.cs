using System;

namespace Nagi.Presentation.Models;

/// <summary>
///     The optional columns a song list can show. Title is always shown.
/// </summary>
[Flags]
public enum SongListColumns
{
    None = 0,
    TrackNumber = 1 << 0,
    Artist = 1 << 1,
    Album = 1 << 2,
    Year = 1 << 3,
    Duration = 1 << 4
}
