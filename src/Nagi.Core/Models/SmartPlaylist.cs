using System.ComponentModel.DataAnnotations;

namespace Nagi.Core.Models;

public class SmartPlaylist
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

    /// <summary>
    ///     Determines how rules are combined.
    ///     True = ALL rules must match (AND logic).
    ///     False = ANY rule can match (OR logic).
    /// </summary>
    public bool MatchAllRules { get; set; } = true;

    public SmartPlaylistSortOrder SortOrder { get; set; } = SmartPlaylistSortOrder.TitleAsc;

    public virtual ICollection<SmartPlaylistRule> Rules { get; set; } = new List<SmartPlaylistRule>();

    public override string ToString()
    {
        return Name;
    }
}
