using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Presentation.Services;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public class TrayIconViewModelTests
{
    private readonly IWindowService _window = Substitute.For<IWindowService>();
    private readonly SettingsService _settings = TestSettings.Create();

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void ClosingTheWindow_HidesToTrayOnlyWhenEnabledOutsideOnboardingAndExit(bool hideToTray,
        bool isShowingOnboarding, bool isExiting, bool expectHidden)
    {
        _settings.Set(AppSettings.HideToTray, hideToTray);
        _window.IsShowingOnboarding.Returns(isShowingOnboarding);
        _window.IsExiting.Returns(isExiting);
        var viewModel = new TrayIconViewModel(_settings, Substitute.For<ITrayPopupService>(), _window,
            Substitute.For<IAppInfoService>(), new ImmediateDispatcher(), NullLogger<TrayIconViewModel>.Instance);
        viewModel.Initialize();

        var closing = new WindowClosingEventArgs();
        _window.Closing += Raise.Event<Action<WindowClosingEventArgs>>(closing);

        closing.Cancel.Should().Be(expectHidden);
        _window.Received(expectHidden ? 1 : 0).Hide();
    }
}
