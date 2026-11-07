using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Jeffijoe.MessageFormat;

namespace Nagi.Presentation.Common;

/// <summary>
///     Formats localized strings, whether a translation uses ICU plural or select rules or plain .NET placeholders.
/// </summary>
public static partial class ResourceFormatter
{
    public static string Format(string pattern, params object?[] args)
    {
        try
        {
            // Only patterns with ICU rules go through MessageFormat: ICU reads apostrophes as quotes, so a plain
            // translation such as "'{0}'" would print the placeholder instead of the argument.
            if (!IcuArgument().IsMatch(pattern)) return string.Format(pattern, args);

            // ICU variables are named, including numeric placeholders such as {0}.
            var namedArgs = new Dictionary<string, object?>(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                namedArgs[i.ToString(CultureInfo.InvariantCulture)] = args[i];
            }

            return MessageFormatter.Format(pattern, namedArgs, CultureInfo.CurrentUICulture);
        }
        catch (Exception)
        {
            // A broken translation shows its pattern rather than crashing the UI.
            return pattern;
        }
    }

    [GeneratedRegex(@"\{\s*\d+\s*,\s*(plural|select|selectordinal)\s*,")]
    private static partial Regex IcuArgument();
}
