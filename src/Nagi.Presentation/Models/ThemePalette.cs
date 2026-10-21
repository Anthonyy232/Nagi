using MaterialColorUtilities.ColorAppearance;
using MaterialColorUtilities.Utils;

namespace Nagi.Presentation.Models;

/// <summary>
///     The colors the app derives from its primary color for the current light or dark theme.
/// </summary>
public readonly record struct ThemePalette(
    ArgbColor Primary,
    ArgbColor MediaOnImageAccent,
    ArgbColor PlayerTint,
    ArgbColor PlayerBackground,
    ArgbColor PlayerAccent)
{
    // Roughly 4.5:1 contrast against the dark media overlay surface (#303030).
    private const double MinimumMediaOnImageLuminance = 0.31;
    private static readonly ArgbColor White = ArgbColor.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue);

    /// <param name="tintIntensity">How far the player tint moves from the theme background towards the primary color, from 0 to 1.</param>
    /// <param name="mediaOnImageSource">The color to derive the accent shown over album art from; <c>null</c> uses the primary color.</param>
    public static ThemePalette Create(ArgbColor primary, bool isLightTheme, double tintIntensity,
        ArgbColor? mediaOnImageSource = null)
    {
        var baseChannel = isLightTheme ? byte.MaxValue : byte.MinValue;
        var playerTint = ArgbColor.FromRgb(
            BlendChannel(baseChannel, primary.R, tintIntensity),
            BlendChannel(baseChannel, primary.G, tintIntensity),
            BlendChannel(baseChannel, primary.B, tintIntensity));

        return new ThemePalette(
            primary,
            GetMediaOnImageAccent(mediaOnImageSource ?? primary),
            playerTint,
            ClampTone(playerTint, isLightTheme ? 85 : 0, isLightTheme ? 100 : 35),
            ClampTone(primary, isLightTheme ? 0 : 80, isLightTheme ? 40 : 100));
    }

    private static ArgbColor ClampTone(ArgbColor color, double minimum, double maximum)
    {
        var hct = Hct.FromInt(ColorUtils.ArgbFromRgb(color.R, color.G, color.B));
        var tone = Math.Clamp(hct.Tone, minimum, maximum);
        if (tone == hct.Tone) return color;

        hct.Tone = tone;
        var argb = hct.ToInt();
        return ArgbColor.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    private static ArgbColor GetMediaOnImageAccent(ArgbColor color)
    {
        var opaqueColor = ArgbColor.FromRgb(color.R, color.G, color.B);
        if (GetRelativeLuminance(opaqueColor) >= MinimumMediaOnImageLuminance) return opaqueColor;

        var low = 0.0;
        var high = 1.0;
        for (var i = 0; i < 8; i++)
        {
            var amount = (low + high) / 2;
            if (GetRelativeLuminance(BlendColors(opaqueColor, White, amount)) >= MinimumMediaOnImageLuminance)
                high = amount;
            else
                low = amount;
        }

        return BlendColors(opaqueColor, White, high);
    }

    private static ArgbColor BlendColors(ArgbColor from, ArgbColor to, double amount) =>
        ArgbColor.FromRgb(
            BlendChannel(from.R, to.R, amount),
            BlendChannel(from.G, to.G, amount),
            BlendChannel(from.B, to.B, amount));

    private static byte BlendChannel(byte from, byte to, double amount) => (byte)(from + ((to - from) * amount));

    internal static double GetRelativeLuminance(ArgbColor color) =>
        ColorUtils.XyzFromArgb(ColorUtils.ArgbFromRgb(color.R, color.G, color.B))[1] / 100;
}
