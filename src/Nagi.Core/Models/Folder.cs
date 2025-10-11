using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

/// <summary>
///     Represents a file system folder being monitored for music files.
///     Supports hierarchical folder structures with parent-child relationships.
/// </summary>
public class Folder
{
    /// <summary>
    ///     The unique identifier for the folder.
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     A user-friendly name for the folder.
    /// </summary>
    [MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     The absolute file system path to the folder.
    /// </summary>
    [Required]
    [MaxLength(1000)]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     The last known modification or scan time of the folder's contents.
    /// </summary>
    public DateTime? LastModifiedDate { get; set; }

    /// <summary>
    ///     The ID of the parent folder. Null if this is a root folder.
    /// </summary>
    public Guid? ParentFolderId { get; set; }

    /// <summary>
    ///     Navigation property to the parent folder in the hierarchy.
    /// </summary>
    [ForeignKey("ParentFolderId")]
    public virtual Folder? ParentFolder { get; set; }

    /// <summary>
    ///     Navigation property to child folders (subfolders) in the hierarchy.
    /// </summary>
    public virtual ICollection<Folder> SubFolders { get; set; } = new List<Folder>();

    /// <summary>
    ///     A collection of songs located directly within this folder (not including subfolders).
    /// </summary>
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    /// <summary>
    ///     Indicates whether this folder is a root-level folder (has no parent).
    /// </summary>
    [NotMapped]
    public bool IsRootFolder => ParentFolderId == null;

    public override string ToString()
    {
        return string.IsNullOrEmpty(Name) ? Path : Name;
    }
}