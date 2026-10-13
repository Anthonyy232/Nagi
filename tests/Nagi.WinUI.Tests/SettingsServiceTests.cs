using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Services;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class SettingsServiceTests
{
    [Theory]
    [InlineData("#8021944B", 128)]
    [InlineData("8021944B", 128)]
    [InlineData("#21944b", 255)]
    [InlineData("21944b", 255)]
    public async Task AccentColor_LoadsLegacyHexAndRoundTripsAlphaWithoutAnAppInstance(string hex, byte alpha)
    {
        var settings = CreateSettings(new Dictionary<string, object> { ["AccentColor"] = hex });
        var expected = new ArgbColor(alpha, 0x21, 0x94, 0x4B);

        (await settings.GetAccentColorAsync()).Should().Be(expected);
        await settings.SetAccentColorAsync(expected);
        (await settings.GetAccentColorAsync()).Should().Be(expected);
        await settings.SetAccentColorAsync(null);
        (await settings.GetAccentColorAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("#GG21944B")]
    public async Task AccentColor_InvalidSavedHexUsesDefault(string hex)
    {
        var settings = CreateSettings(new Dictionary<string, object> { ["AccentColor"] = hex });
        (await settings.GetAccentColorAsync()).Should().BeNull();
    }

    [Fact]
    public async Task ResetStoredValues_NotifiesLiveServicesBeforeRemovingSavedValues()
    {
        var values = new Dictionary<string, object> { ["ObsoleteSetting"] = true };
        var settings = CreateSettings(values);
        await settings.SetVolumeNormalizationEnabledAsync(!SettingsDefaults.VolumeNormalizationEnabled);
        await settings.SetHideToTrayEnabledAsync(!SettingsDefaults.HideToTrayEnabled);
        await settings.SetLanguageAsync("ja");
        await settings.SetIgnoreLeadingArticlesOnSortEnabledAsync(!SettingsDefaults.IgnoreLeadingArticlesOnSortEnabled);
        var notifications = new List<string>();
        settings.VolumeNormalizationEnabledChanged += value => notifications.Add($"volume:{value}");
        settings.HideToTraySettingChanged += value => notifications.Add($"tray:{value}");
        settings.IgnoreLeadingArticlesOnSortEnabledChanged += value => notifications.Add($"articles:{value}");

        await settings.ResetStoredValuesAsync();

        notifications.Should().BeEquivalentTo(
            $"volume:{SettingsDefaults.VolumeNormalizationEnabled}", $"tray:{SettingsDefaults.HideToTrayEnabled}",
            $"articles:{SettingsDefaults.IgnoreLeadingArticlesOnSortEnabled}");
        values.Should().BeEmpty();
        (await settings.GetVolumeNormalizationEnabledAsync()).Should().Be(SettingsDefaults.VolumeNormalizationEnabled);
        (await settings.GetLanguageAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Providers_ReconcileSavedPriorityWithCurrentDefinitionsWithoutRewritingStorage()
    {
        var saved = JsonSerializer.Serialize(new[]
        {
            new ServiceProviderSetting { Id = ServiceProviderIds.NetEase.ToUpperInvariant(), DisplayName = "Old", IsEnabled = false, Order = int.MaxValue },
            new ServiceProviderSetting { Id = ServiceProviderIds.NetEase, IsEnabled = true, Order = 0 },
            new ServiceProviderSetting { Id = "Removed", IsEnabled = true, Order = -1 }
        });
        var values = new Dictionary<string, object> { ["LyricsServiceProviders"] = saved };
        var settings = CreateSettings(values);

        var providers = await settings.GetServiceProvidersAsync(ServiceCategory.Lyrics);

        providers.Select(p => p.Id).Should().Equal(ServiceProviderIds.NetEase, ServiceProviderIds.LrcLib);
        providers.Select(p => p.Order).Should().Equal(0, 1);
        providers[0].IsEnabled.Should().BeFalse();
        providers[0].DisplayName.Should().Be("NetEase");
        providers.Should().OnlyContain(p => p.Category == ServiceCategory.Lyrics);
        values["LyricsServiceProviders"].Should().Be(saved);
        (await settings.GetEnabledServiceProvidersAsync(ServiceCategory.Lyrics)).Select(p => p.Id)
            .Should().Equal(ServiceProviderIds.LrcLib);
    }

    [Fact]
    public async Task Navigation_UsesCanonicalTagsAndCurrentLabelsWhilePreservingOrderAndVisibility()
    {
        var values = new Dictionary<string, object>
        {
            ["NavigationItems"] = JsonSerializer.Serialize(new[]
            {
                new NavigationItemSetting { Tag = "albums", DisplayName = "Old", IsEnabled = false },
                new NavigationItemSetting { Tag = "ALBUMS", IsEnabled = true },
                new NavigationItemSetting { Tag = "Removed" },
                new NavigationItemSetting { Tag = "Library", DisplayName = "Old", IsEnabled = true }
            })
        };
        var settings = CreateSettings(values);

        var items = await settings.GetNavigationItemsAsync();

        items.Select(item => item.Tag).Should().Equal("Albums", "Library", "Folders", "Playlists", "Artists", "Genres", "Insights", "Favorites");
        items.Single(item => item.Tag == "Favorites").IsEnabled.Should().BeFalse();
        items[0].IsEnabled.Should().BeFalse();
        items.Should().OnlyContain(item => item.DisplayName != "Old" && !string.IsNullOrEmpty(item.IconGlyph));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayerButtons_PreserveSavedLayoutAndMigrateMissingDivider(bool hasDivider)
    {
        var saved = new List<PlayerButtonSetting>
        {
            new() { Id = "shuffle", IsEnabled = false, DisplayName = "Old", IconGlyph = "Old" },
            new() { Id = "Shuffle", IsEnabled = true, DisplayName = "Old", IconGlyph = "Old" },
            new() { Id = "Next", IsEnabled = true, DisplayName = "Old", IconGlyph = "Old" },
            new() { Id = "Removed", DisplayName = "Old", IconGlyph = "Old" }
        };
        if (hasDivider) saved.Insert(0, new PlayerButtonSetting
        { Id = "Separator", IsEnabled = false, DisplayName = "Old", IconGlyph = "Old" });
        var settings = CreateSettings(new Dictionary<string, object>
        { ["PlayerButtonSettings"] = JsonSerializer.Serialize(saved) });

        var buttons = await settings.GetPlayerButtonSettingsAsync();

        buttons.Select(button => button.Id).Should().Equal(hasDivider
            ? ["Separator", "Shuffle", "Next"] : ["Shuffle", "Next", "Separator"]);
        buttons.Single(button => button.Id == "Shuffle").IsEnabled.Should().BeFalse();
        buttons.Single(button => button.Id == "Separator").IsEnabled.Should().Be(!hasDivider);
        buttons.Should().OnlyContain(button => button.DisplayName != "Old");
    }

    [Fact]
    public async Task InvalidStoredEnumAndJson_FallBackToDefaults()
    {
        var settings = CreateSettings(new Dictionary<string, object>
        { ["MusicRepeatMode"] = "999", ["LyricsServiceProviders"] = "{broken" });

        (await settings.GetInitialRepeatModeAsync()).Should().Be(SettingsDefaults.DefaultRepeatMode);
        (await settings.GetServiceProvidersAsync(ServiceCategory.Lyrics)).Select(provider => provider.Id)
            .Should().Equal(ServiceProviderIds.LrcLib, ServiceProviderIds.NetEase);
    }

    [Fact]
    public async Task PaneState_ReadsLegacyStringsAndWritesNativeBooleans()
    {
        var values = new Dictionary<string, object> { ["LastPaneOpen"] = "false" };
        var settings = CreateSettings(values);

        (await settings.GetLastPaneOpenAsync()).Should().BeFalse();
        await settings.SetLastPaneOpenAsync(true);
        values["LastPaneOpen"].Should().BeOfType<bool>().Which.Should().BeTrue();
        (await settings.GetLastPaneOpenAsync()).Should().BeTrue();
        values.Clear();
        (await settings.GetLastPaneOpenAsync()).Should().BeNull();
    }

    [Fact]
    public async Task PlaybackState_FailedReplacementPreservesSavedQueueAndCleansTemporaryFile()
    {
        var directory = Directory.CreateTempSubdirectory("Nagi-settings-");
        try
        {
            var path = Path.Combine(directory.FullName, "playback_state.json");
            var settings = CreateSettings(new Dictionary<string, object>(), path);
            var songId = Guid.NewGuid();
            await settings.SavePlaybackStateAsync(new PlaybackState
            { CurrentTrackId = songId, PlaybackQueueTrackIds = [songId] });

            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failure = await Record.ExceptionAsync(() => settings.SavePlaybackStateAsync(new PlaybackState()));
                Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            }

            var state = await settings.GetPlaybackStateAsync();
            state!.CurrentTrackId.Should().Be(songId);
            state.PlaybackQueueTrackIds.Should().Equal(songId);
            Directory.GetFiles(directory.FullName).Should().Equal(path);
            await settings.SavePlaybackStateAsync(null);
            (await settings.GetPlaybackStateAsync()).Should().BeNull();
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PlaybackState_ReadFailurePreservesFileButMalformedJsonIsDiscarded()
    {
        var directory = Directory.CreateTempSubdirectory("Nagi-settings-");
        try
        {
            var path = Path.Combine(directory.FullName, "playback_state.json");
            var settings = CreateSettings(new Dictionary<string, object>(), path);
            await settings.SavePlaybackStateAsync(new PlaybackState());
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                (await settings.GetPlaybackStateAsync()).Should().BeNull();
            File.Exists(path).Should().BeTrue();

            await File.WriteAllTextAsync(path, "{broken");
            (await settings.GetPlaybackStateAsync()).Should().BeNull();
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static SettingsService CreateSettings(IDictionary<string, object> values, string? playbackPath = null)
    {
        var paths = Substitute.For<IPathConfiguration>();
        paths.PlaybackStateFilePath.Returns(playbackPath ?? "unused.json");
        return new SettingsService(paths, Substitute.For<ICredentialLockerService>(),
            NullLogger<SettingsService>.Instance, values);
    }
}
