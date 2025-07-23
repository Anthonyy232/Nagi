namespace Nagi.Services.Presence;

/// <summary>
/// Provides helpful extension methods for string manipulation.
/// </summary>
public static class StringExtensions {
    /// <summary>
    /// Truncates a string to a maximum length.
    /// </summary>
    /// <param name="value">The string to truncate.</param>
    /// <param name="maxLength">The maximum length of the string.</param>
    /// <returns>The truncated string, or the original string if it's shorter than the max length.</returns>
    public static string Truncate(this string value, int maxLength) {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}