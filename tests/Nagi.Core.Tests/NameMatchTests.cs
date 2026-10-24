using FluentAssertions;
using Nagi.Core.Helpers;
using Xunit;

namespace Nagi.Core.Tests;

public class NameMatchTests
{
    [Theory]
    [InlineData("Beyoncé", "BEYONCE", true)]
    [InlineData("AC/DC", "AC-DC", true)]
    [InlineData("Ｙｅｓ", "Yes", true)]
    [InlineData("Muse", "Muse Tribute", false)]
    [InlineData("!!!", "!!!", true)]
    [InlineData("!!!", "???", false)]
    [InlineData("", "", false)]
    public void AreEquivalent_IgnoresCatalogFormattingButNotDifferentNames(string first, string second, bool expected)
    {
        NameMatch.AreEquivalent(first, second).Should().Be(expected);
    }
}
