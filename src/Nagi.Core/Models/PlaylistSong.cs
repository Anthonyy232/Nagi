namespace Nagi.Core.Models;

public class PlaylistSong
{
    public Guid PlaylistId { get; set; }

    public virtual Playlist Playlist { get; set; } = null!;

    public Guid SongId { get; set; }

    public virtual Song Song { get; set; } = null!;

    public double Order { get; set; }
}
