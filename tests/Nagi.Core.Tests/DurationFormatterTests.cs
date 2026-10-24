using FluentAssertions;
using Nagi.Core.Helpers;
using Xunit;

namespace Nagi.Core.Tests;

public class DurationFormatterTests
{
    [Theory]
    [InlineData(65, "1:05")]
    [InlineData(3600, "1:00:00")]
    [InlineData(25 * 3600 + 61, "25:01:01")]
    [InlineData(100 * 3600 + 30 * 60 + 15, "100h 30m")]
    public void Format_SwitchesFormatAtHourBoundaries(int seconds, string expected)
    {
        DurationFormatter.Format(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }
}
