using System;
using System.Collections.Generic;
using System.Globalization;
using Jeffijoe.MessageFormat;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Formats localized ICU plural rules and standard .NET placeholders.
/// </summary>
public static class ResourceFormatter
{
    public static string Format(string pattern, params object[] args)
    {
        try
        {
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
            // .NET format specifiers such as {0:N0} are not ICU syntax.
            try
            {
                return string.Format(pattern, args);
            }
            catch (FormatException)
            {
                // Last resort: return the pattern itself to avoid crashing the UI
                return pattern;
            }
        }
    }
}
