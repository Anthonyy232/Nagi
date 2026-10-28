using FluentAssertions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Xunit;

namespace Nagi.Presentation.Tests;

public class DialogInputRuleTests
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

    [Theory]
    [InlineData(0, true)]
    [InlineData(365, true)]
    [InlineData(-1, false)]
    [InlineData(366, false)]
    [InlineData(double.NaN, false)]
    public void IsValidAvoidRecentDays_AcceptsWholeDaysUpToAYear(double days, bool expected) =>
        PlaybackMixOptions.IsValidAvoidRecentDays(days).Should().Be(expected);

    [Theory]
    [InlineData("Road Trip", true)]
    [InlineData(" Mix ", false)]
    [InlineData("   ", false)]
    public void CanRename_RequiresANewNonBlankName(string newName, bool expected) =>
        PlaylistNames.CanRename(newName, "Mix").Should().Be(expected);

    [Theory]
    [InlineData("line one\r\nline two", false)]
    [InlineData("line one\nline three", true)]
    [InlineData("  ", false)]
    public void LyricsCanSave_IgnoresLineEndingChanges(string edited, bool expected) =>
        LyricsEdit.CanSave(edited, "line one\nline two").Should().Be(expected);
}
