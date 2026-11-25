using System.Text.Json;
using Nagi.Core.Platform;

namespace Nagi.Core.Library;

/// <summary>
///     A file's tags beyond the standard fields, as <see cref="Song.Tags" /> stores them: a JSON object of values by name.
/// </summary>
public static class SongTags
{
    public const int MaxValueLength = 1000;

    /// <summary>
    ///     A tag's name as it is stored and matched, or null for a blank one. Tag names ignore case, and a JSON path
    ///     can't match a quote or a backslash.
    /// </summary>
    public static string? NormalizeName(string? name) =>
        TextNormalizer.Normalize(name?.Replace("\"", string.Empty).Replace("\\", string.Empty))?.ToUpperInvariant();

    /// <param name="tags">Values by <see cref="NormalizeName">normalized</see> name.</param>
    public static string? Serialize(IReadOnlyDictionary<string, string> tags) =>
        tags.Count == 0 ? null : JsonSerializer.Serialize(tags);

    public static IReadOnlyDictionary<string, string> Parse(string? json) =>
        json is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

    internal static string PathOf(string name) => $"$.\"{name}\"";
}
