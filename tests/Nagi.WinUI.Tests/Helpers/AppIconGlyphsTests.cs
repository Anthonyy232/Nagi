using FluentAssertions;
using Nagi.Presentation.Common;
using Nagi.WinUI.Helpers;
using Xunit;

namespace Nagi.WinUI.Tests.Helpers;

public class AppIconGlyphsTests
{
    [Fact]
    public void EveryIcon_HasAGlyph()
    {
        Enum.GetValues<AppIcon>().Where(icon => icon != AppIcon.None)
            .Should().OnlyContain(icon => AppIconGlyphs.ToGlyph(icon).Length > 0);
    }
}
