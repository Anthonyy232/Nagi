using AwesomeAssertions;
using Nagi.Core.Playback;
using Nagi.Presentation.Player;
using Xunit;

namespace Nagi.Presentation.Tests.Player;

public class PlayForDialogViewModelTests
{
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
}
