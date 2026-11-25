using AwesomeAssertions;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Shell;
using Nagi.Presentation.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Shell;

public class SupportPromptViewModelTests
{
    private readonly SettingsService _settings = TestSettings.Create();
    private readonly IUIService _uiService = Substitute.For<IUIService>();
    private readonly IAppInfoService _appInfo = Substitute.For<IAppInfoService>();

    private SupportPromptViewModel Launch() => new(_settings, _uiService, _appInfo);

    private SupportPromptViewModel LaunchUntilAsked()
    {
        for (var launch = 1; launch < SupportPromptViewModel.LaunchesBeforeAsking; launch++)
            Launch().IsOpen.Should().BeFalse();
        return Launch();
    }

    [Fact]
    public void Asks_FromTheFifthLaunch_UntilAnswered()
    {
        LaunchUntilAsked().IsOpen.Should().BeTrue();
        var prompt = Launch();
        prompt.IsOpen.Should().BeTrue();

        prompt.DismissCommand.Execute(null);

        prompt.IsOpen.Should().BeFalse();
        Launch().IsOpen.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, AppLinks.Repository)]
    [InlineData(true, AppLinks.StoreReview)]
    public async Task OpenPage_OpensWhereNagiCameFrom_AndNeverAsksAgain(bool isStoreInstall, string page)
    {
        _appInfo.IsStoreInstall.Returns(isStoreInstall);

        await LaunchUntilAsked().OpenPageCommand.ExecuteAsync(null);

        await _uiService.Received(1).OpenUriAsync(new Uri(page));
        Launch().IsOpen.Should().BeFalse();
    }
}
