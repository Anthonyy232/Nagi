using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;

public class EqualizerServiceTests
{
    private readonly IAudioPlayer _audioPlayer = Substitute.For<IAudioPlayer>();
    private readonly SettingsService _settings = TestSettings.Create();
    private readonly EqualizerService _service;

    public EqualizerServiceTests()
    {
        _audioPlayer.GetEqualizerBandsAsync().Returns(new List<(uint, float)> { (0, 60f), (1, 170f) });
        _service = new EqualizerService(_audioPlayer, _settings, NullLogger<EqualizerService>.Instance);
    }

    [Fact]
    public async Task InitializeAsync_WithSavedSettingsForOtherBands_StartsFlatAndAppliesIt()
    {
        _settings.Set(CoreSettings.Equalizer, new EqualizerSettings { Preamp = 3, BandGains = [1, 2, 3] });

        await _service.InitializeAsync();

        _service.Bands.Should().HaveCount(2);
        _service.CurrentSettings!.BandGains.Should().Equal(0, 0);
        _service.CurrentSettings.Preamp.Should().Be(EqualizerSettings.DefaultPreampDb);
        _settings.Get(CoreSettings.Equalizer).Should().BeEquivalentTo(_service.CurrentSettings);
        await _audioPlayer.Received(1).ApplyEqualizerSettingsAsync(_service.CurrentSettings);
    }

    [Fact]
    public async Task InitializeAsync_WhenThePlayerFailsToStart_LeavesTheEqualizerEmpty()
    {
        _audioPlayer.GetEqualizerBandsAsync().ThrowsAsync(new InvalidOperationException("LibVLC missing"));

        await _service.InitializeAsync();

        _service.Bands.Should().BeEmpty();
        _service.CurrentSettings.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingABandOrThePreamp_ChangesOnlyThatValueAndSavesIt(bool preamp)
    {
        await _service.InitializeAsync();
        var bandsBefore = _service.CurrentSettings!.BandGains.ToList();
        var preampBefore = _service.CurrentSettings.Preamp;
        _audioPlayer.ClearReceivedCalls();
        var changes = 0;
        _service.SettingsChanged += () => changes++;

        if (preamp) await _service.SetPreampAsync(5.0f);
        else await _service.SetBandGainAsync(1, 3.5f);

        var after = _service.CurrentSettings!;
        after.Preamp.Should().Be(preamp ? 5.0f : preampBefore);
        after.BandGains.Should().Equal(preamp ? bandsBefore : bandsBefore.Select((gain, i) => i == 1 ? 3.5f : gain));
        await _audioPlayer.Received(1).ApplyEqualizerSettingsAsync(after);
        _settings.Get(CoreSettings.Equalizer).Should().BeEquivalentTo(after);
        changes.Should().Be(1);
    }

    [Fact]
    public async Task ResetAsync_FlattensEveryBandAndRestoresTheDefaultPreamp()
    {
        await _service.InitializeAsync();
        await _service.SetBandGainAsync(0, 5.0f);
        await _service.SetPreampAsync(2.0f);
        _audioPlayer.ClearReceivedCalls();

        await _service.ResetAsync();

        _service.CurrentSettings!.Preamp.Should().Be(EqualizerSettings.DefaultPreampDb);
        _service.CurrentSettings.BandGains.Should().AllSatisfy(g => g.Should().Be(0.0f));
        await _audioPlayer.Received(1).ApplyEqualizerSettingsAsync(_service.CurrentSettings);
        _settings.Get(CoreSettings.Equalizer).Should().BeEquivalentTo(_service.CurrentSettings);
    }
}
