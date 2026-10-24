using System.Globalization;

namespace Nagi.Core.Helpers;

/// <summary>
///     Decides whether a name from an online catalog refers to the same thing as a library name.
/// </summary>
public static class NameMatch
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
}
