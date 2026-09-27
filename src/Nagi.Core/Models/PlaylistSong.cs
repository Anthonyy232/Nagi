namespace Nagi.Core.Models;

public class PlaylistSong
{
    public Guid PlaylistId { get; set; }

    public virtual Playlist Playlist { get; set; } = null!;

    public Guid SongId { get; set; }

    public virtual Song Song { get; set; } = null!;

    /// <summary>
    ///    The display order of the song within the playlist.
    /// </summary>
    public double Order { get; set; }
}
