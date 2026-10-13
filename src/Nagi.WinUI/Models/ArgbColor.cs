using System.Globalization;

namespace Nagi.WinUI.Models;

public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    public static ArgbColor FromRgb(byte r, byte g, byte b) => new(byte.MaxValue, r, g, b);

    /// <summary>
    ///     Parses a <c>#RRGGBB</c> or <c>#AARRGGBB</c> color; the leading <c>#</c> is optional.
    /// </summary>
    public static bool TryParseHex(string? hex, out ArgbColor color)
    {
        color = default;
        if (string.IsNullOrEmpty(hex)) return false;

        hex = hex.TrimStart('#');
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)) return false;

        switch (hex.Length)
        {
            case 8:
                color = new ArgbColor((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
                return true;
            case 6:
                color = FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
                return true;
            default:
                return false;
        }
    }

    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
