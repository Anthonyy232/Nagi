using FluentAssertions;
using Nagi.Core.Helpers;
using Xunit;

namespace Nagi.Core.Tests;

public class SortKeyHelperTests
{
    [Theory]
    [InlineData("The Beatles", "beatles")]
    [InlineData("the beatles", "beatles")]
    [InlineData("THE BEATLES", "beatles")]
    [InlineData("A Day in the Life", "day in the life")]
    [InlineData("An Evening", "evening")]
    [InlineData("Theater", "theater")]
    [InlineData("  The  Wall  ", "wall")]
    [InlineData("The", "the")]
    [InlineData("Carpenters", "carpenters")]
    [InlineData("A The Hobbit", "the hobbit")]
    public void Normalize_StripsLeadingArticlesAndLowercases(string input, string expected)
    {
        SortKeyHelper.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_EmptyOrWhitespace_ReturnsEmpty(string? input)
    {
        SortKeyHelper.Normalize(input).Should().Be(string.Empty);
    }

}
