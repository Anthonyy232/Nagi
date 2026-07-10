namespace Nagi.Core.Helpers;

/// <summary>
/// Builds SQL LIKE patterns that treat user-entered wildcard characters literally.
/// </summary>
public static class LikePatternHelper
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
