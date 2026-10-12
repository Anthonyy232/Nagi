using System.ComponentModel.DataAnnotations;

namespace Nagi.Core.Models;

public class Genre
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Format(Resources.Strings.Format_Unknown, Resources.Strings.Label_Genre);

    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    public override string ToString()
    {
        return Name;
    }
}
