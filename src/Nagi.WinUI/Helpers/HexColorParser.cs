using System.Globalization;
using Microsoft.UI;
using Windows.UI;

namespace Nagi.WinUI.Helpers;

internal static class HexColorParser
{
    /// <summary>
    ///     Parses a <c>#RRGGBB</c> or <c>#AARRGGBB</c> color; the leading <c>#</c> is optional.
    /// </summary>
    internal static bool TryParse(string hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)) return false;

        if (hex.Length == 8) // AARRGGBB
        {
            color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }

        if (hex.Length == 6) // RRGGBB
        {
            color = Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }

        return false;
    }
}
