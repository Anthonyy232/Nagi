using System.ComponentModel.DataAnnotations;

namespace Nagi.Core.Models;

public class Playlist
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(500)]
    public string Name { get; set; } = string.Format(Resources.Strings.Format_New, Resources.Strings.Label_Playlist);

    /// <summary>
    ///     The date and time the playlist was created.
    /// </summary>
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     The date and time the playlist was last modified.
    /// </summary>
    public DateTime DateModified { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     An optional description for the playlist.
    /// </summary>
    [MaxLength(5000)]
    public string? Description { get; set; }

    /// <summary>
    ///     A URI or path to a custom cover image for the playlist.
    /// </summary>
    [MaxLength(2000)]
    public string? CoverImageUri { get; set; }

    /// <summary>
    ///     The join entities that link this playlist to its songs in an ordered sequence.
    /// </summary>
    public virtual ICollection<PlaylistSong> PlaylistSongs { get; set; } = new List<PlaylistSong>();

    public override string ToString()
    {
        return Name;
    }
}
