using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nagi.Core.Artwork;
using Nagi.Core.Library;

namespace Nagi.Core.Playlists;

public class SmartPlaylist
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    public DateTime DateCreated { get; set; } = DateTime.UtcNow;

    public DateTime DateModified { get; set; } = DateTime.UtcNow;

    [MaxLength(5000)]
    public string? Description { get; set; }

    [MaxLength(2000)]
    public string? CoverImageUri { get; set; }

    /// <summary>The cover for display, or null when the playlist has none.</summary>
    [NotMapped]
    public string? CoverUri => ArtworkUri.Versioned(CoverImageUri, DateModified);

    public bool MatchAllRules { get; set; } = true;

    public SongSortOrder SortOrder { get; set; } = SongSortOrder.TitleAsc;

    [MaxLength(200)]
    public string? SortTag { get; set; }

    /// <summary>Whether the navigation menu lists the playlist.</summary>
    public bool IsPinned { get; set; }

    public virtual ICollection<SmartPlaylistRule> Rules { get; set; } = new List<SmartPlaylistRule>();

    public override string ToString()
    {
        return Name;
    }
}
