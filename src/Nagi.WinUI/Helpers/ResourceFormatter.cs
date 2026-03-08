using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var formatter = _formatters.GetOrAdd(culture.Name, _ => new MessageFormatter(culture: culture, useCache: true));

            // MessageFormat uses ICU-style named variables, even when patterns use numeric names like {0, plural, ...}.
            // Passing a raw object[] is not supported — we must convert args to a Dictionary<string, object?>
            // where keys are the string representations of the indices ("0", "1", etc.).
            var namedArgs = new Dictionary<string, object?>(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                namedArgs[i.ToString()] = args[i];
            }

            return formatter.FormatMessage(pattern, namedArgs);
        }
        catch (Exception)
        {
            // Fallback to standard string.Format for non-ICU patterns (e.g. plain "{0}" style).
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
