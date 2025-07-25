namespace Nagi.Core.Models;

/// <summary>
///     Represents the join entity between a Playlist and a Song,
///     establishing a many-to-many relationship with an explicit order.
/// </summary>
public class PlaylistSong
{
    /// <summary>
    ///     The foreign key for the associated Playlist.
    /// </summary>
    public Guid PlaylistId { get; set; }

    /// <summary>
    ///     The navigation property to the associated Playlist.
    /// </summary>
    public virtual Playlist Playlist { get; set; } = null!;

    /// <summary>
    ///     The foreign key for the associated Song.
    /// </summary>
    public Guid SongId { get; set; }

    /// <summary>
    ///     The navigation property to the associated Song.
    /// </summary>
    public virtual Song Song { get; set; } = null!;

    /// <summary>
    ///     The zero-based position of the song within the playlist.
    /// </summary>
    public int Order { get; set; }
}