using FluentAssertions;
using MaterialColorUtilities.ColorAppearance;
using MaterialColorUtilities.Utils;
using Nagi.Presentation.Models;
using Xunit;

namespace Nagi.Presentation.Tests;

public class ThemePaletteTests
{
    [Fact]
    public void DarkSourceColor_IsLightenedJustEnoughToReadOverAlbumArt()
    {
        var palette = ThemePalette.Create(ArgbColor.FromRgb(20, 10, 60), isLightTheme: false, tintIntensity: 1);

        ThemePalette.GetRelativeLuminance(palette.MediaOnImageAccent).Should().BeInRange(0.31, 0.34);
    }

    [Fact]
    public void BrightSourceColor_IsKeptOverAlbumArt()
    {
        var yellow = ArgbColor.FromRgb(250, 220, 40);

        ThemePalette.Create(ArgbColor.FromRgb(0, 0, 0), isLightTheme: false, tintIntensity: 1, yellow)
            .MediaOnImageAccent.Should().Be(yellow);
    }

    [Theory]
    [InlineData(true, 85, 100, 0, 40)]
    [InlineData(false, 0, 35, 80, 100)]
    public void PlayerColors_StayWithinTheThemeToneRange(bool isLightTheme, double minBackground,
        double maxBackground, double minAccent, double maxAccent)
    {
        var palette = ThemePalette.Create(ArgbColor.FromRgb(120, 200, 90), isLightTheme, tintIntensity: 1);

        Tone(palette.PlayerBackground).Should().BeInRange(minBackground - 0.5, maxBackground + 0.5);
        Tone(palette.PlayerAccent).Should().BeInRange(minAccent - 0.5, maxAccent + 0.5);
    }

    private static double Tone(ArgbColor color) => Hct.FromInt(ColorUtils.ArgbFromRgb(color.R, color.G, color.B)).Tone;
}
