using System;
using System.Collections.Concurrent;
using Jeffijoe.MessageFormat;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Provides helper methods for formatting strings using ICU MessageFormat syntax.
///     This is required for languages like Russian, Czech, and Polish that use complex pluralization rules.
/// </summary>
public static class ResourceFormatter
{
    // Cache formatters by locale name to ensure correct pluralization rules are applied.
    // e.g., "en-US", "ru-RU", "cs-CZ"
    private static readonly ConcurrentDictionary<string, MessageFormatter> _formatters = new();

    /// <summary>
    ///     Formats a string using ICU MessageFormat syntax.
    ///     Automatically handles standard .NET format strings ({0}) as well.
    /// </summary>
    /// <param name="pattern">The format pattern (e.g. "{count, plural, one {# item} other {# items}}").</param>
    /// <param name="args">The arguments to format with.</param>
    /// <returns>The formatted string.</returns>
    public static string Format(string pattern, params object[] args)
    {
        if (string.IsNullOrEmpty(pattern)) return string.Empty;

        try
        {
            var locale = System.Globalization.CultureInfo.CurrentUICulture.Name;
            var formatter = _formatters.GetOrAdd(locale, l => new MessageFormatter(locale: l, useCache: true));
            return formatter.FormatMessage(pattern, args);
        }
        catch (Exception)
        {
            // Fallback to standard string.Format if ICU parsing fails
            // This ensures we don't crash if the string is just a standard format string that MessageFormat happens to dislike,
            // or if something else goes wrong.
            try
            {
                return string.Format(pattern, args);
            }
            catch
            {
                // Last resort: return the pattern itself to avoid crashing the UI
                return pattern;
            }
        }
    }
}
