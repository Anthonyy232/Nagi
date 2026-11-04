using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Nagi.Core.Helpers;

public static partial class UrlRedaction
{
    /// <summary>
    ///     Replaces every URL query in <paramref name="text" /> with an ellipsis, because the URL a music server song
    ///     streams from carries the server's credentials in its query. A query is recognized by its key=value pairs,
    ///     so request lines without a scheme or host are covered too.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? RemoveQueries(string? text) => text is null ? null : UrlQueryRegex().Replace(text, "?…");

    [GeneratedRegex(@"\?[^\s""'<>=?]*=[^\s""'<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex UrlQueryRegex();
}
