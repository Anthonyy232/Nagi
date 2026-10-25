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

    [Theory]
    [InlineData("Abbey Road", "Abbey Road (Remastered 2009)", true)]
    [InlineData("Random Access Memories", "Random Access Memories [10th Anniversary Edition] (Deluxe)", true)]
    [InlineData("Get Lucky - Single", "Get Lucky", true)]
    [InlineData("(What's the Story) Morning Glory?", "(What's The Story) Morning Glory? (Remastered)", true)]
    [InlineData("(I Can't Get No) Satisfaction", "Satisfaction", false)]
    [InlineData("Abbey Road", "Let It Be (Remastered)", false)]
    public void AreSameAlbum_IgnoresTrailingEditionLabels(string first, string second, bool expected)
    {
        NameMatch.AreSameAlbum(first, second).Should().Be(expected);
    }
}
