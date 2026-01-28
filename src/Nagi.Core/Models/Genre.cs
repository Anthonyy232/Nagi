using System.ComponentModel.DataAnnotations;

namespace Nagi.Core.Models;

/// <summary>
///     Represents a musical genre.
/// </summary>
public class Genre
{
    /// <summary>
    ///     The unique identifier for the genre.
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     The name of the genre (e.g., "Rock", "Jazz", "Electronic").
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Format(Resources.Strings.Format_Unknown, Resources.Strings.Label_Genre);

    /// <summary>
    ///     The collection of songs associated with this genre.
    /// </summary>
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    /// <summary>
    ///     Returns the name of the genre.
    /// </summary>
    /// <returns>The name of the genre.</returns>
    public override string ToString()
    {
        return Name;
    }
}