using System.Globalization;
using FluentAssertions;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Converters;
using Nagi.WinUI.Resources;
using Xunit;

namespace Nagi.WinUI.Tests;

public class ValueConverterTests
{
    [Fact]
    public void ThemeConverter_AfterCultureChange_UsesCurrentLabelsInBothDirections()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var converter = new ElementThemeToFriendlyStringConverter();
            var english = (string)converter.Convert(ElementTheme.Light, typeof(string), null!, "en-US");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Strings.Theme_Light.Should().NotBe(english);
            converter.Convert(ElementTheme.Light, typeof(string), null!, "fr-FR").Should().Be(Strings.Theme_Light);
            converter.ConvertBack(Strings.Theme_Light, typeof(ElementTheme), null!, "fr-FR")
                .Should().Be(ElementTheme.Light);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
