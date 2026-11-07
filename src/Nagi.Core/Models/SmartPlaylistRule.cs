using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

public class SmartPlaylistRule
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SmartPlaylistId { get; set; }

    [ForeignKey("SmartPlaylistId")]
    public virtual SmartPlaylist SmartPlaylist { get; set; } = null!;

    public SmartPlaylistField Field { get; set; }

    public SmartPlaylistOperator Operator { get; set; }

    /// <summary>
    ///     In the form <see cref="Helpers.SmartPlaylistRuleValue.ToStoredValue" /> gives. <see cref="SecondValue" /> is
    ///     the upper bound of a range.
    /// </summary>
    [MaxLength(1000)]
    public string? Value { get; set; }

    [MaxLength(1000)]
    public string? SecondValue { get; set; }

    public int Order { get; set; }
}
