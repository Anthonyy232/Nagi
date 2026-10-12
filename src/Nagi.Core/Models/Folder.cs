using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

public class Folder
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(1000)]
    public string Path { get; set; } = string.Empty;

    public DateTime? LastModifiedDate { get; set; }

    /// <summary>
    ///     The ID of the parent folder. Null if this is a root folder.
    /// </summary>
    public Guid? ParentFolderId { get; set; }

    [ForeignKey("ParentFolderId")]
    public virtual Folder? ParentFolder { get; set; }

    public virtual ICollection<Folder> SubFolders { get; set; } = new List<Folder>();

    /// <summary>
    ///     Songs assigned to this monitored root, including songs in its subdirectories.
    /// </summary>
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    [NotMapped]
    public bool IsRootFolder => ParentFolderId == null;

    public override string ToString()
    {
        return string.IsNullOrEmpty(Name) ? Path : Name;
    }
}
