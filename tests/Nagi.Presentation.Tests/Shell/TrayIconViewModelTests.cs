using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Settings;
using Nagi.Presentation.Shell;
using Nagi.Presentation.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Shell;

public class TrayIconViewModelTests
{
    private readonly SettingsService _settings = TestSettings.Create();
    private readonly ITrayPopupService _popup = Substitute.For<ITrayPopupService>();
    private readonly IWindowService _window = Substitute.For<IWindowService>();
    private readonly TrayIconViewModel _viewModel;

    public TrayIconViewModelTests() =>
        _viewModel = new TrayIconViewModel(_settings, _popup, _window, Substitute.For<IAppInfoService>(),
            new ImmediateDispatcher(), NullLogger<TrayIconViewModel>.Instance);

    // Without the tray icon, a hidden window would have no way back.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TurningHideToTrayOff_RemovesTheIconAndShowsAWindowThatWasHidden(bool isWindowVisible)
    {
        _settings.Set(AppSettings.HideToTray, true);
        _window.IsVisible.Returns(isWindowVisible);
        _viewModel.Initialize();
        _viewModel.IsTrayIconVisible.Should().Be(!isWindowVisible);

        _settings.Set(AppSettings.HideToTray, false);

        _viewModel.IsTrayIconVisible.Should().BeFalse();
        _window.Received(isWindowVisible ? 0 : 1).ShowAndActivate();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShowPopup_OpensThePopupOnlyWhileTheWindowIsHidden(bool isWindowVisible)
    {
        _window.IsVisible.Returns(isWindowVisible);

        _viewModel.ShowPopupCommand.Execute(null);

        _popup.Received(isWindowVisible ? 0 : 1).TogglePopup();
        _popup.Received(isWindowVisible ? 1 : 0).HidePopup();
    }
}
