using System.ComponentModel.DataAnnotations;

namespace Nagi.Core.Models;

/// <summary>
///     Represents a file system folder being monitored for music files.
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
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     The absolute file system path to the folder.
    /// </summary>
    [Required]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     The last known modification or scan time of the folder's contents.
    /// </summary>
    public DateTime? LastModifiedDate { get; set; }

    /// <summary>
    ///     A collection of songs located within this folder.
    /// </summary>
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    public override string ToString()
    {
        return string.IsNullOrEmpty(Name) ? Path : Name;
    }
}