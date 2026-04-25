using System.Globalization;

namespace Nagi.Core.Helpers;

/// <summary>
///     Produces normalized sort keys for titles and names. When article-stripping is enabled,
///     leading English articles ("the", "a", "an") are removed so "The Beatles" sorts under "B".
/// </summary>
public static class SortKeyHelper
{
    private static readonly string[] LeadingArticles = { "the ", "a ", "an " };

    /// <summary>
    ///     Returns a sort key for <paramref name="value"/> with leading articles stripped
    ///     and whitespace trimmed. Case-folds to lowercase invariant for stable ordering.
    ///     Returns an empty string when <paramref name="value"/> is null or whitespace.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var trimmed = value.Trim();
        foreach (var article in LeadingArticles)
        {
            if (trimmed.Length > article.Length &&
                trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[article.Length..].Trim();
                break;
            }
        }

        return trimmed.ToLower(CultureInfo.InvariantCulture);
    }
}
