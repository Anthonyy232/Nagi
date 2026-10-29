using System.Globalization;
using Nagi.Core.Models;

namespace Nagi.Core.Helpers;

public static class SmartPlaylistRuleValue
{
    public static string? ToStoredValue(SmartPlaylistField field, SmartPlaylistOperator ruleOperator, string? value)
    {
        if (field is SmartPlaylistField.Bpm or SmartPlaylistField.Duration &&
            TryParseNumber(value, out var number))
            return number.ToString("R", CultureInfo.InvariantCulture);

        if (IsAbsoluteDate(field, ruleOperator) && TryParseDate(value, out var date))
            return date.ToString("O", CultureInfo.InvariantCulture);

        return value;
    }

    public static string ToDisplayValue(SmartPlaylistField field, SmartPlaylistOperator ruleOperator, string? value)
    {
        if (field is SmartPlaylistField.Bpm or SmartPlaylistField.Duration &&
            TryParseNumber(value, out var number))
            return number.ToString("R", CultureInfo.CurrentCulture);

        if (IsAbsoluteDate(field, ruleOperator) && TryParseDate(value, out var date))
            return date.ToString(date.TimeOfDay == TimeSpan.Zero ? "d" : "g", CultureInfo.CurrentCulture);

        return value ?? string.Empty;
    }

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

    private static bool IsAbsoluteDate(SmartPlaylistField field, SmartPlaylistOperator ruleOperator) =>
        field is SmartPlaylistField.DateAdded or SmartPlaylistField.LastPlayed or
            SmartPlaylistField.FileCreatedDate or SmartPlaylistField.FileModifiedDate &&
        ruleOperator is SmartPlaylistOperator.GreaterThan or SmartPlaylistOperator.LessThan or SmartPlaylistOperator.IsInRange;
}
