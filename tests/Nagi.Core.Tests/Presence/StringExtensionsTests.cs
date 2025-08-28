using FluentAssertions;
using Nagi.Core.Services.Implementations.Presence;
using Xunit;

namespace Nagi.Core.Tests.Presence;

/// <summary>
///     Contains unit tests for the <see cref="StringExtensions" /> static class.
/// </summary>
public class StringExtensionsTests
{
    /// <summary>
    ///     Verifies that <see cref="StringExtensions.Truncate" /> returns the original string if its
    ///     length is less than the specified maximum length.
    /// </summary>
    [Fact]
    public void Truncate_WhenStringIsShorterThanMaxLength_ReturnsOriginalString()
    {
        // Arrange
        const string original = "hello";
        const int maxLength = 10;

        // Act
        var result = original.Truncate(maxLength);

        // Assert
        result.Should().Be(original);
    }

    /// <summary>
    ///     Verifies that <see cref="StringExtensions.Truncate" /> returns the original string if its
    ///     length is equal to the specified maximum length.
    /// </summary>
    [Fact]
    public void Truncate_WhenStringIsEqualToMaxLength_ReturnsOriginalString()
    {
        // Arrange
        const string original = "hello";
        const int maxLength = 5;

        // Act
        var result = original.Truncate(maxLength);

        // Assert
        result.Should().Be(original);
    }

    /// <summary>
    ///     Verifies that <see cref="StringExtensions.Truncate" /> correctly shortens a string that is
    ///     longer than the specified maximum length.
    /// </summary>
    [Fact]
    public void Truncate_WhenStringIsLongerThanMaxLength_ReturnsTruncatedString()
    {
        // Arrange
        const string original = "hello world";
        const int maxLength = 5;
        const string expected = "hello";

        // Act
        var result = original.Truncate(maxLength);

        // Assert
        result.Should().Be(expected);
    }

    /// <summary>
    ///     Verifies that <see cref="StringExtensions.Truncate" /> handles empty strings
    ///     correctly by returning the original value without throwing an exception.
    /// </summary>
    [Theory]
    [InlineData("")]
    public void Truncate_WhenStringIsEmpty_ReturnsOriginalString(string value)
    {
        // Arrange
        const int maxLength = 5;

        // Act
        var result = value.Truncate(maxLength);

        // Assert
        result.Should().Be(value);
    }

    /// <summary>
    ///     Verifies that <see cref="StringExtensions.Truncate" /> returns an empty string when the
    ///     maximum length is zero.
    /// </summary>
    [Fact]
    public void Truncate_WhenMaxLengthIsZero_ReturnsEmptyString()
    {
        // Arrange
        const string original = "hello";
        const int maxLength = 0;

        // Act
        var result = original.Truncate(maxLength);

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    ///     Verifies that <see cref="StringExtensions.Truncate" /> throws an
    ///     <see cref="ArgumentOutOfRangeException" /> when the maximum length is negative, which is
    ///     the expected behavior of the underlying <see cref="string.Substring(int, int)" /> method.
    /// </summary>
    [Fact]
    public void Truncate_WhenMaxLengthIsNegative_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        const string original = "hello";
        const int maxLength = -1;

        // Act
        Action act = () => original.Truncate(maxLength);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}