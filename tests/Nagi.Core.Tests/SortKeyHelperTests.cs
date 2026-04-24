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
    [InlineData("Theater", "theater")]          // "The" without trailing space is not an article.
    [InlineData("  The  Wall  ", "wall")]       // Whitespace around and after the article.
    [InlineData("The", "the")]                  // Article alone is not stripped (nothing would remain).
    [InlineData("Carpenters", "carpenters")]
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

    [Fact]
    public void Normalize_OnlyStripsFirstArticle()
    {
        // "A The Hobbit" → drop leading "a ", leave "The Hobbit" alone (only one pass).
        SortKeyHelper.Normalize("A The Hobbit").Should().Be("the hobbit");
    }
}
