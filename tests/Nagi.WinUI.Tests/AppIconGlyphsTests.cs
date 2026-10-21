using FluentAssertions;
using Nagi.Presentation.Models;
using Nagi.WinUI.Helpers;
using Xunit;

namespace Nagi.WinUI.Tests;

public class AppIconGlyphsTests
{
    [Fact]
    public void EveryIcon_HasAGlyph()
    {
        Enum.GetValues<AppIcon>().Where(icon => icon != AppIcon.None)
            .Should().OnlyContain(icon => AppIconGlyphs.ToGlyph(icon).Length > 0);
    }
}
