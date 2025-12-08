using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

/// <summary>
///     Represents a single rule within a smart playlist.
///     Each rule defines a condition that songs must match to be included.
/// </summary>
public class SmartPlaylistRule
{
    /// <summary>
    ///     The unique identifier for this rule.
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     The foreign key for the parent smart playlist.
    /// </summary>
    public Guid SmartPlaylistId { get; set; }

    /// <summary>
    ///     Navigation property to the parent smart playlist.
    /// </summary>
    [ForeignKey("SmartPlaylistId")]
    public virtual SmartPlaylist SmartPlaylist { get; set; } = null!;

    /// <summary>
    ///     The song field being evaluated (e.g., Artist, Genre, PlayCount).
    /// </summary>
    public SmartPlaylistField Field { get; set; }

    /// <summary>
    ///     The comparison operator (e.g., Is, Contains, GreaterThan).
    /// </summary>
    public SmartPlaylistOperator Operator { get; set; }

    /// <summary>
    ///     The primary value to compare against.
    ///     Stored as string and parsed based on the field type.
    /// </summary>
    [MaxLength(1000)]
    public string? Value { get; set; }

    /// <summary>
    ///     Secondary value for range operators (e.g., "is in the range X to Y").
    /// </summary>
    [MaxLength(1000)]
    public string? SecondValue { get; set; }

    /// <summary>
    ///     The display order of this rule within the smart playlist.
    /// </summary>
    public int Order { get; set; }
}
