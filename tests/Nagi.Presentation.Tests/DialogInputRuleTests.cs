using FluentAssertions;
using Nagi.Core.Playback;
using Nagi.Presentation.Player;
using Nagi.Presentation.ViewModels;
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
    [InlineData(ShuffleMode.MoreVariety, 0, true)]
    [InlineData(ShuffleMode.MoreVariety, 365, true)]
    [InlineData(ShuffleMode.MoreVariety, -1, false)]
    [InlineData(ShuffleMode.MoreVariety, 366, false)]
    [InlineData(ShuffleMode.MoreVariety, double.NaN, false)]
    [InlineData(ShuffleMode.Rediscover, double.NaN, true)]
    public void PlayFor_ChecksAvoidedDaysOnlyForTheModeThatOffersThem(ShuffleMode mode, double days, bool expected)
    {
        var viewModel = new PlayForDialogViewModel { AvoidRecentDays = days };
        viewModel.SelectedMode = viewModel.ModeOptions.Single(option => option.Value == mode);

        viewModel.CanAccept.Should().Be(expected);
    }

    [Theory]
    [InlineData("Road Trip", true)]
    [InlineData(" Mix ", false)]
    [InlineData("   ", false)]
    public void Rename_RequiresANewNonBlankName(string newName, bool expected) =>
        new PlaylistNameDialogViewModel("Mix") { Name = newName }.CanAccept.Should().Be(expected);

    [Theory]
    [InlineData("line one\r\nline two", false)]
    [InlineData("line one\nline three", true)]
    [InlineData("  ", false)]
    public void LyricsEditor_IgnoresLineEndingChanges(string edited, bool expected) =>
        new LyricsEditorDialogViewModel("line one\nline two", canRemove: false) { Lyrics = edited }.CanSave
            .Should().Be(expected);
}
