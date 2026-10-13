using System.Globalization;
using FluentAssertions;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.WinUI.Converters;
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
            var converter = new AppThemeToFriendlyStringConverter();
            var english = (string)converter.Convert(AppTheme.Light, typeof(string), null!, "en-US");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Strings.Theme_Light.Should().NotBe(english);
            converter.Convert(AppTheme.Light, typeof(string), null!, "fr-FR").Should().Be(Strings.Theme_Light);
            converter.ConvertBack(Strings.Theme_Light, typeof(AppTheme), null!, "fr-FR")
                .Should().Be(AppTheme.Light);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
