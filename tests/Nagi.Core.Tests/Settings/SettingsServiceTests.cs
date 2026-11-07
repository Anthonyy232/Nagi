using System.Text.Json;
using FluentAssertions;
using Nagi.Core.OnlineProviders;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Settings;

public class SettingsServiceTests
{
    [Fact]
    public void Set_NotifiesOnlyWhenTheEffectiveValueChanges()
    {
        var settings = TestSettings.Create();
        var changes = new List<Setting>();
        settings.SettingChanged += changes.Add;

        settings.Set(CoreSettings.Volume, 1.5);
        settings.Set(CoreSettings.Volume, 1.0);
        settings.Set(CoreSettings.IsMuted, false);
        settings.Set(CoreSettings.LyricsProviders, settings.Get(CoreSettings.LyricsProviders));
        settings.Set(CoreSettings.RepeatMode, RepeatMode.RepeatAll);

        changes.Should().Equal(CoreSettings.Volume, CoreSettings.RepeatMode);
        settings.Get(CoreSettings.Volume).Should().Be(1.0);
    }

    [Fact]
    public async Task ResetToDefaults_NotifiesChangedSettingsAfterRemovingSavedValues()
    {
        var values = new Dictionary<string, object> { ["ObsoleteSetting"] = true };
        var settings = TestSettings.Create(values);
        settings.Set(CoreSettings.VolumeNormalization, true);
        settings.Set(CoreSettings.IgnoreLeadingArticlesOnSort, false);
        settings.Set(CoreSettings.FetchOnlineLyrics, CoreSettings.FetchOnlineLyrics.Default);
        var notifications = new List<string>();
        settings.SettingChanged += setting => notifications.Add($"{setting.Key}:{values.ContainsKey(setting.Key)}");

        await settings.ResetToDefaultsAsync();

        notifications.Should().BeEquivalentTo("VolumeNormalizationEnabled:False", "IgnoreLeadingArticlesOnSort:False");
        values.Should().NotContainKey("ObsoleteSetting");
        settings.Get(CoreSettings.VolumeNormalization).Should().BeFalse();
        settings.Get(CoreSettings.IgnoreLeadingArticlesOnSort).Should().BeTrue();
    }

    [Fact]
    public async Task ResetToDefaults_SignsOutClearsThePlaybackSessionAndTurnsOffAutoLaunch()
    {
        var credentials = Substitute.For<ICredentialLockerService>();
        var autoLaunch = Substitute.For<IAutoLaunchService>();
        var playbackState = Substitute.For<IPlaybackStateStore>();
        var settings = TestSettings.Create(credentials: credentials, autoLaunch: autoLaunch, playbackStateStore: playbackState);
        var credentialChanges = 0;
        settings.CredentialsChanged += _ => credentialChanges++;

        await settings.ResetToDefaultsAsync();

        await autoLaunch.Received(1).SetEnabledAsync(false);
        playbackState.Received(1).Clear();
        credentials.Received(1).RemoveCredential("Nagi/LastFm");
        credentials.Received(1).RemoveCredential("Nagi/ListenBrainz");
        credentialChanges.Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavingCredentials_ReportsWhetherTheCredentialStoreAcceptedThem(bool accepted)
    {
        var credentials = Substitute.For<ICredentialLockerService>();
        credentials.SaveCredential(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(accepted);
        var settings = TestSettings.Create(credentials: credentials);
        var credentialChanges = 0;
        settings.CredentialsChanged += _ => credentialChanges++;

        (await settings.SaveLastFmCredentialsAsync("user", "session")).Should().Be(accepted);
        (await settings.SaveListenBrainzUserTokenAsync("token")).Should().Be(accepted);

        credentialChanges.Should().Be(accepted ? 2 : 0);
    }

    [Fact]
    public void Providers_ReconcileSavedPriorityWithCurrentDefinitionsWithoutRewritingStorage()
    {
        var saved = JsonSerializer.Serialize(new[]
        {
            new ServiceProviderSetting { Id = ServiceProviderIds.NetEase.ToUpperInvariant(), DisplayName = "Old", IsEnabled = false, Order = int.MaxValue },
            new ServiceProviderSetting { Id = ServiceProviderIds.NetEase, IsEnabled = true, Order = 0 },
            new ServiceProviderSetting { Id = "Removed", IsEnabled = true, Order = -1 }
        });
        var values = new Dictionary<string, object> { ["LyricsServiceProviders"] = saved };
        var settings = TestSettings.Create(values);

        var providers = settings.Get(CoreSettings.LyricsProviders);

        providers.Select(p => p.Id).Should().Equal(ServiceProviderIds.LrcLib, ServiceProviderIds.NetEase);
        providers.Select(p => p.Order).Should().Equal(0, 1);
        providers[1].IsEnabled.Should().BeFalse();
        providers[1].DisplayName.Should().Be("NetEase");
        providers.Should().OnlyContain(p => p.Category == ServiceCategory.Lyrics);
        values["LyricsServiceProviders"].Should().Be(saved);
    }

    [Fact]
    public void Providers_MissingFromASavedOrderTakeTheirDefaultSlot()
    {
        var saved = JsonSerializer.Serialize(new[]
        {
            new ServiceProviderSetting { Id = ServiceProviderIds.MusicBrainz, Order = 0 },
            new ServiceProviderSetting { Id = ServiceProviderIds.LastFm, Order = 1 }
        });
        var settings = TestSettings.Create(new Dictionary<string, object> { ["MetadataServiceProviders"] = saved });

        var providers = settings.Get(CoreSettings.MetadataProviders);

        // The saved two agree with the default ranking, so every source missing from it lands in its default slot.
        providers.Select(p => p.Id).Should().Equal(CoreSettings.MetadataProviders.Default.Select(p => p.Id));
    }

    [Fact]
    public void Providers_MissingFromASavedOrderStayBelowSourcesTheUserRankedDifferently()
    {
        // The pre-update default order, which ranked TheAudioDB above Fanart.tv.
        var saved = JsonSerializer.Serialize(new[]
        {
            new ServiceProviderSetting { Id = ServiceProviderIds.MusicBrainz, Order = 0 },
            new ServiceProviderSetting { Id = ServiceProviderIds.TheAudioDb, Order = 1 },
            new ServiceProviderSetting { Id = ServiceProviderIds.FanartTv, Order = 2 },
            new ServiceProviderSetting { Id = ServiceProviderIds.LastFm, Order = 3 }
        });
        var settings = TestSettings.Create(new Dictionary<string, object> { ["MetadataServiceProviders"] = saved });

        var ids = settings.Get(CoreSettings.MetadataProviders).Select(p => p.Id).ToList();

        ids.IndexOf(ServiceProviderIds.TheAudioDb).Should().BeLessThan(ids.IndexOf(ServiceProviderIds.FanartTv));
        ids.IndexOf(ServiceProviderIds.Deezer).Should().BeGreaterThan(ids.IndexOf(ServiceProviderIds.FanartTv));
        ids.IndexOf(ServiceProviderIds.AppleMusic).Should().BeGreaterThan(ids.IndexOf(ServiceProviderIds.FanartTv));
    }

    [Fact]
    public void InvalidStoredValues_FallBackToDefaults()
    {
        var settings = TestSettings.Create(new Dictionary<string, object>
        {
            ["MusicRepeatMode"] = "999",
            ["LyricsServiceProviders"] = "{broken",
            ["AppVolume"] = "loud",
            ["EqualizerSettings"] = "{broken"
        });

        settings.Get(CoreSettings.RepeatMode).Should().Be(CoreSettings.RepeatMode.Default);
        settings.Get(CoreSettings.LyricsProviders).Select(provider => provider.Id)
            .Should().Equal(ServiceProviderIds.LrcLib, ServiceProviderIds.NetEase);
        settings.Get(CoreSettings.Volume).Should().Be(CoreSettings.Volume.Default);
        settings.Get(CoreSettings.Equalizer).Should().BeNull();
    }

    [Fact]
    public void ListenBrainzEnabledSince_IsStoredAsUtcTicksWithZeroForUnset()
    {
        var values = new Dictionary<string, object>();
        var settings = TestSettings.Create(values);
        var timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        settings.Set(CoreSettings.ListenBrainzEnabledSinceUtc, timestamp);
        values["ListenBrainzEnabledSinceUtcTicks"].Should().Be(timestamp.Ticks);
        settings.Get(CoreSettings.ListenBrainzEnabledSinceUtc).Should().Be(timestamp);

        settings.Set(CoreSettings.ListenBrainzEnabledSinceUtc, null);
        values["ListenBrainzEnabledSinceUtcTicks"].Should().Be(0L);
        settings.Get(CoreSettings.ListenBrainzEnabledSinceUtc).Should().BeNull();
    }

    [Fact]
    public async Task ExportThenImport_RestoresEveryValueWithItsType_ButNotTheLastFmAuthToken()
    {
        var exported = new Dictionary<string, object>
        {
            ["AppMuteState"] = true,
            ["FadeInDurationMs"] = 3,
            ["ListenBrainzEnabledSinceUtcTicks"] = 638_000_000_000_000_000L,
            ["AppVolume"] = 1.0,
            ["MusicRepeatMode"] = "RepeatAll",
            ["LastFmAuthToken"] = "temporary"
        };
        var snapshot = await TestSettings.Create(exported).ExportAsync();
        var restored = new Dictionary<string, object> { ["Stale"] = "removed" };

        await TestSettings.Create(restored).ImportAsync(snapshot);

        exported.Remove("LastFmAuthToken");
        // Boxed values compare by type too, so 1.0 restored as an int would fail.
        restored.Should().Equal(exported);
    }

    [Theory]
    [InlineData("{\"AppVolume\":0.5}")]
    [InlineData("{\"AppVolume\":{\"Type\":\"Double\",\"Value\":\"loud\"}}")]
    [InlineData("{\"When\":{\"Type\":\"DateTimeOffset\",\"Value\":\"2026-01-01\"}}")]
    public async Task Import_WithAnInvalidSnapshot_ThrowsAndKeepsCurrentSettings(string snapshot)
    {
        var values = new Dictionary<string, object> { ["AppVolume"] = 0.8 };

        await TestSettings.Create(values).Invoking(settings => settings.ImportAsync(snapshot)).Should().ThrowAsync<JsonException>();

        values.Should().Equal(new Dictionary<string, object> { ["AppVolume"] = 0.8 });
    }
}
