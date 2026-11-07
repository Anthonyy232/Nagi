namespace Nagi.Core.Library;

/// <summary>
/// Builds SQL LIKE patterns that treat user-entered wildcard characters literally.
/// </summary>
internal static class LikePatternHelper
{
    public const string EscapeCharacter = "!";

    public static string CreateContainsPattern(string value)
    {
        var escaped = value
            .Replace(EscapeCharacter, EscapeCharacter + EscapeCharacter, StringComparison.Ordinal)
            .Replace("%", EscapeCharacter + "%", StringComparison.Ordinal)
            .Replace("_", EscapeCharacter + "_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }
}
