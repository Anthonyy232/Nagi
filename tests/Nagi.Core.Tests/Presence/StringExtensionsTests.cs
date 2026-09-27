using FluentAssertions;
using Nagi.Core.Helpers;
using Xunit;

namespace Nagi.Core.Tests.Presence;

public class StringExtensionsTests
{
    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello world", 5, "hello")]
    [InlineData("", 5, "")]
    [InlineData("hello", 0, "")]
    public void Truncate_RespectsMaximumLength(string value, int maxLength, string expected)
    {
        value.Truncate(maxLength).Should().Be(expected);
    }
}
