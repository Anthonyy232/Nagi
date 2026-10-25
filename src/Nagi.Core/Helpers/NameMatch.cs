using System.Globalization;
using System.Text.RegularExpressions;

namespace Nagi.Core.Helpers;

/// <summary>
///     Decides whether a name from an online catalog refers to the same thing as a library name.
/// </summary>
public static partial class NameMatch
{
    // Catalogs and tags disagree on case, accents, punctuation and full-width forms ("Beyoncé", "AC/DC").
    private const CompareOptions LooseOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace |
                                                CompareOptions.IgnoreSymbols | CompareOptions.IgnoreWidth |
                                                CompareOptions.IgnoreKanaType;

    public static bool AreEquivalent(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;

        // Ignoring symbols would make names made only of symbols ("!!!") match each other.
        if (!first.Any(char.IsLetterOrDigit) || !second.Any(char.IsLetterOrDigit))
            return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

        return CultureInfo.InvariantCulture.CompareInfo.Compare(first, second, LooseOptions) == 0;
    }

    /// <summary>
    ///     Like <see cref="AreEquivalent" />, but ignores edition labels that catalogs append to album titles,
    ///     such as "(Remastered 2011)", "[Deluxe Edition]" or " - Single".
    /// </summary>
    public static bool AreSameAlbum(string? first, string? second) =>
        AreEquivalent(first is null ? null : WithoutEditionLabel(first), second is null ? null : WithoutEditionLabel(second));

    /// <summary>Removes trailing edition labels such as "(Remastered 2011)"; a title that is only a label is kept.</summary>
    public static string WithoutEditionLabel(string title)
    {
        var stripped = EditionSuffixRegex().Replace(title, string.Empty);
        return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
    }

    [GeneratedRegex(@"(\s*(\([^()]*\)|\[[^\[\]]*\])|\s+-\s+(Single|EP))+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EditionSuffixRegex();
}
