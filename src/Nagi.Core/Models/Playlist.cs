using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

public class Playlist
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(500)]
    public string Name { get; set; } = string.Format(Resources.Strings.Format_New, Resources.Strings.Label_Playlist);

    public DateTime DateCreated { get; set; } = DateTime.UtcNow;

    public DateTime DateModified { get; set; } = DateTime.UtcNow;

    [MaxLength(5000)]
    public string? Description { get; set; }

    [MaxLength(2000)]
    public string? CoverImageUri { get; set; }

    public virtual ICollection<PlaylistSong> PlaylistSongs { get; set; } = new List<PlaylistSong>();

    /// <summary>
    ///     The root folder of the music server the playlist comes from, or null for a playlist made in Nagi. A server's
    ///     playlists follow the server at each sync, so they can't be changed in Nagi.
    /// </summary>
    public Guid? ServerFolderId { get; set; }

    [ForeignKey(nameof(ServerFolderId))] public virtual Folder? ServerFolder { get; set; }

    /// <summary>
    ///     The server's ID for the playlist; null for a playlist made in Nagi.
    /// </summary>
    [MaxLength(200)] public string? RemoteId { get; set; }

    [NotMapped] public bool IsFromServer => ServerFolderId is not null;

    public override string ToString()
    {
        return Name;
    }
}
