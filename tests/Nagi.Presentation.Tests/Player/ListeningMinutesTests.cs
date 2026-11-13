using FluentAssertions;
using Nagi.Presentation.Player;
using Xunit;

namespace Nagi.Presentation.Tests.Player;

public class ListeningMinutesTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(1440, true)]
    [InlineData(0, false)]
    [InlineData(1441, false)]
    [InlineData(1.5, false)]
    [InlineData(double.NaN, false)]
    public void ListeningMinutes_AcceptsWholeMinutesUpToADay(double minutes, bool expected) =>
        ListeningMinutes.IsValid(minutes).Should().Be(expected);
}
