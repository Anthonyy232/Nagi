using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Settings;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;
using Nagi.Presentation.Settings;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public class WindowBehaviorTests
{
    private readonly IWindowService _window = Substitute.For<IWindowService>();
    private readonly IApplicationLifecycle _lifecycle = Substitute.For<IApplicationLifecycle>();
    private readonly SettingsService _settings = TestSettings.Create();
    private readonly WindowBehavior _behavior;

    public WindowBehaviorTests() =>
        _behavior = new WindowBehavior(_window, _lifecycle, _settings, new ImmediateDispatcher(),
            NullLogger<WindowBehavior>.Instance);

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void ClosingTheWindow_HidesToTrayOnlyWhenEnabledOutsideOnboardingAndExit(bool hideToTray,
        bool isShowingOnboarding, bool isExiting, bool expectHidden)
    {
        _settings.Set(AppSettings.HideToTray, hideToTray);
        _lifecycle.IsShowingOnboarding.Returns(isShowingOnboarding);
        _window.IsExiting.Returns(isExiting);

        var closing = new WindowClosingEventArgs();
        _window.Closing += Raise.Event<Action<WindowClosingEventArgs>>(closing);

        closing.Cancel.Should().Be(expectHidden);
        _window.Received(expectHidden ? 1 : 0).Hide();
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void MinimizingTheWindow_OpensTheMiniPlayerOnlyWhenEnabledOutsideOnboarding(bool minimizeToMiniPlayer,
        bool isShowingOnboarding, bool expectMiniPlayer)
    {
        _settings.Set(AppSettings.MinimizeToMiniPlayer, minimizeToMiniPlayer);
        _lifecycle.IsShowingOnboarding.Returns(isShowingOnboarding);

        _window.Minimized += Raise.Event<Action>();

        _window.Received(expectMiniPlayer ? 1 : 0).ShowMiniPlayer();
    }

    [Theory]
    [InlineData(false, false, true, true, "window")]
    [InlineData(true, true, true, true, "window")]
    [InlineData(true, false, true, true, "mini player")]
    [InlineData(true, false, false, false, "minimized")]
    [InlineData(true, false, false, true, "tray")]
    public void ShowWhenLoaded_ShowsWhatTheStartupSettingsAsk(bool startMinimized, bool activatedExternally,
        bool miniPlayer, bool hideToTray, string expected)
    {
        _settings.Set(AppSettings.StartMinimized, startMinimized);
        _settings.Set(AppSettings.MinimizeToMiniPlayer, miniPlayer);
        _settings.Set(AppSettings.HideToTray, hideToTray);

        _behavior.ShowWhenLoaded(activatedExternally);

        _window.Received(expected == "window" ? 1 : 0).ShowAndActivate();
        _window.Received(expected == "mini player" ? 1 : 0).ShowMiniPlayer();
        _window.Received(expected == "minimized" ? 1 : 0).ShowMinimized();
    }
}
