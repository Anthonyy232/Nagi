using System.ComponentModel.DataAnnotations;

namespace Nagi.Core.Models;

public class SmartPlaylist
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(500)]
    public string Name { get; set; } = string.Format(Resources.Strings.Format_New, Resources.Strings.Label_Playlist);

    /// <summary>
    ///     The date and time the smart playlist was created.
    /// </summary>
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     The date and time the smart playlist was last modified.
    /// </summary>
    public DateTime DateModified { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     An optional description for the smart playlist.
    /// </summary>
    [MaxLength(5000)]
    public string? Description { get; set; }

    /// <summary>
    ///     A URI or path to a custom cover image for the smart playlist.
    /// </summary>
    [MaxLength(2000)]
    public string? CoverImageUri { get; set; }

    /// <summary>
    ///     Determines how rules are combined.
    ///     True = ALL rules must match (AND logic).
    ///     False = ANY rule can match (OR logic).
    /// </summary>
    public bool MatchAllRules { get; set; } = true;

    /// <summary>
    ///     Sort order for songs in the smart playlist.
    /// </summary>
    public SmartPlaylistSortOrder SortOrder { get; set; } = SmartPlaylistSortOrder.TitleAsc;

    /// <summary>
    ///     The collection of rules that define which songs are included in this smart playlist.
    /// </summary>
    public virtual ICollection<SmartPlaylistRule> Rules { get; set; } = new List<SmartPlaylistRule>();

    public override string ToString()
    {
        return Name;
    }
}
