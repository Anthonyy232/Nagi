using System.Globalization;
using Nagi.Core.Models;

namespace Nagi.Core.Helpers;

public static class SmartPlaylistRuleValue
{
    private enum Format
    {
        Plain,
        Number,
        Date
    }

    /// <summary>
    ///     Converts a value typed in the editor to the culture-invariant form a rule is stored and matched with:
    ///     round-trip numbers, ISO 8601 dates, and otherwise NFC-normalized, trimmed text (null when blank).
    /// </summary>
    public static string? ToStoredValue(SmartPlaylistField field, SmartPlaylistOperator ruleOperator, string? value) =>
        FormatOf(field, ruleOperator) switch
        {
            Format.Number when TryParseNumber(value, out var number) => number.ToString("R", CultureInfo.InvariantCulture),
            Format.Date when TryParseDate(value, out var date) => date.ToString("O", CultureInfo.InvariantCulture),
            _ => TextNormalizer.Normalize(value)
        };

    public static string ToDisplayValue(SmartPlaylistField field, SmartPlaylistOperator ruleOperator, string? value) =>
        FormatOf(field, ruleOperator) switch
        {
            Format.Number when TryParseNumber(value, out var number) => number.ToString("R", CultureInfo.CurrentCulture),
            Format.Date when TryParseDate(value, out var date) =>
                date.ToString(date.TimeOfDay == TimeSpan.Zero ? "d" : "g", CultureInfo.CurrentCulture),
            _ => value ?? string.Empty
        };

    public static bool TryParseNumber(string? value, out double number) =>
        (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
         double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number)) && double.IsFinite(number);

    public static bool TryParseDate(string? value, out DateTime date)
    {
        var parsed = DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date) ||
                     DateTime.TryParse(value, CultureInfo.CurrentCulture,
                         DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out date);
        if (parsed && date.Kind == DateTimeKind.Local) date = date.ToUniversalTime();
        return parsed;
    }

    private static Format FormatOf(SmartPlaylistField field, SmartPlaylistOperator ruleOperator) =>
        SmartPlaylistFields.KindOf(field) switch
        {
            SmartPlaylistFieldKind.Decimal => Format.Number,
            SmartPlaylistFieldKind.Date when ruleOperator is SmartPlaylistOperator.GreaterThan
                or SmartPlaylistOperator.LessThan or SmartPlaylistOperator.IsInRange => Format.Date,
            _ => Format.Plain
        };
}
