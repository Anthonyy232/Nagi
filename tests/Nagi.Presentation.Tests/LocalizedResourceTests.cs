using System.Globalization;
using FluentAssertions;
using Nagi.Presentation.Helpers;
using Xunit;
using CoreStrings = Nagi.Core.Resources.Strings;
using UiStrings = Nagi.Presentation.Resources.Strings;

namespace Nagi.Presentation.Tests;

public class LocalizedResourceTests
{
    [Fact]
    public void Formatter_UsesCurrentUiCultureForCachedPatternsAndSupportsNetSpecifiers()
    {
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            const string pattern = "{0, plural, one {# item} other {# items}}";
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            ResourceFormatter.Format(pattern, 21).Should().Be("21 item");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
            ResourceFormatter.Format(pattern, 21).Should().Be("21 items");

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            ResourceFormatter.Format("{0:N0}", 1234).Should().Be("1,234");
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void GeneratedAccessors_FollowUiCultureAndFallBackToNeutralResources()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            CoreStrings.Label_Title.Should().Be("Titel");
            UiStrings.Label_Composer.Should().Be("Komponist");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-AU");
            CoreStrings.Label_Title.Should().Be("Title");
            UiStrings.Label_Composer.Should().Be("Composer");
            UiStrings.Label_Grouping.Should().Be("Grouping");
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
