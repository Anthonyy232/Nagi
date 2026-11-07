using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using Nagi.Presentation.Settings;
using Nagi.Presentation.Theme;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public class ThemeCoordinatorTests
{
    private readonly IThemeService _theme = Substitute.For<IThemeService>();
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly SettingsService _settings = TestSettings.Create();
    private readonly List<ThemePalette> _appliedPalettes = [];

    public ThemeCoordinatorTests()
    {
        _theme.IsLightTheme.Returns(true);
        _theme.When(theme => theme.SetColors(Arg.Any<ThemePalette>()))
            .Do(call => _appliedPalettes.Add(call.Arg<ThemePalette>()));
        new ThemeCoordinator(_theme, _settings, _playback, new ImmediateDispatcher(),
            NullLogger<ThemeCoordinator>.Instance).Start();
    }

    [Fact]
    public void ChangingTheAccent_WithDynamicThemingOnAndNoTrackSwatch_RecolorsWithTheNewAccent()
    {
        _settings.Set(AppSettings.DynamicTheming, true);
        var accent = ArgbColor.FromRgb(200, 10, 20);

        _settings.Set(AppSettings.AccentColor, accent);

        _appliedPalettes.Last().Should().Be(ThemePalette.ForAccent(accent, isLightTheme: true,
            _settings.Get(AppSettings.PlayerTintIntensity)));
    }

    [Fact]
    public void ChangingTheTrack_WithDynamicThemingOn_RecolorsFromItsSwatch()
    {
        _settings.Set(AppSettings.DynamicTheming, true);
        _playback.CurrentTrack.Returns(new Song { LightSwatchId = "#336699", DarkSwatchId = "#112233" });

        _playback.TrackChanged += Raise.Event<Action>();

        _appliedPalettes.Last().Primary.Should().Be(ArgbColor.FromRgb(0x33, 0x66, 0x99));
    }

    [Fact]
    public void ChangingTheTheme_AppliesItBeforeRecoloringForTheThemeShown()
    {
        _theme.ClearReceivedCalls();

        _settings.Set(AppSettings.Theme, AppTheme.Dark);

        Received.InOrder(() =>
        {
            _theme.SetTheme(AppTheme.Dark);
            _ = _theme.IsLightTheme;
            _theme.SetColors(Arg.Any<ThemePalette>());
        });
    }
}
