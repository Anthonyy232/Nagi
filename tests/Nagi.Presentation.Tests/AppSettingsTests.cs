using System.Text.Json;
using FluentAssertions;
using Nagi.Core.Services.Data;
using Nagi.Core.Settings;
using Nagi.Presentation.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Services;
using Xunit;

namespace Nagi.Presentation.Tests;

public class AppSettingsTests
{
    // Existing users' settings are saved under these keys, so they must never change.
    [Fact]
    public void EverySetting_KeepsItsStoreKey()
    {
        var keys = Setting.DeclaredIn(typeof(CoreSettings)).Concat(Setting.DeclaredIn(typeof(AppSettings)))
            .Select(setting => setting.Key).ToList();

        keys.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(
            "AppVolume", "AppMuteState", "MusicShuffleState", "MusicRepeatMode", "RestorePlaybackStateEnabled",
            "EqualizerSettings", "VolumeNormalizationEnabled", "WasapiExclusiveModeEnabled", "AudioOutputDeviceId",
            "FadeOnPlayPauseEnabled", "FadeInDurationMs", "FadeOutDurationMs", "FetchOnlineMetadataEnabled",
            "FetchOnlineLyricsEnabled", "LyricsRomanizationEnabled", "IgnoreLeadingArticlesOnSort",
            "ArtistSplitCharacters", "GenreSplitCharacters", "LyricsServiceProviders", "MetadataServiceProviders",
            "DiscordRichPresenceEnabled", "LastFmScrobblingEnabled", "LastFmNowPlayingEnabled", "LastFmAuthToken",
            "ListenBrainzScrobblingEnabled", "ListenBrainzNowPlayingEnabled", "ListenBrainzServerUrl",
            "ListenBrainzEnabledSinceUtcTicks",
            "AppTheme", "BackdropMaterial", "DynamicThemingEnabled", "AccentColor", "PlayerBackgroundMaterial",
            "PlayerTintIntensity", "AppLanguage", "PlayerAnimationEnabled", "StartMinimizedEnabled",
            "HideToTrayEnabled", "MinimizeToMiniPlayerEnabled", "ShowCoverArtInTrayFlyout", "NavigationItems",
            "PlayerButtonSettings", "QueueDisplaySettings", "SongListColumns", "ShowSongListHeader", "SongsPerPage",
            "RememberWindowSizeEnabled", "LastWindowSize", "RememberWindowPositionEnabled", "LastWindowPosition",
            "RememberPaneStateEnabled", "LastPaneOpen", "SortOrder_Library", "SortOrder_Albums", "SortOrder_Artists",
            "SortOrder_Genres", "SortOrder_Playlists", "SortOrder_FolderView", "SortOrder_AlbumView",
            "SortOrder_ArtistView", "SortOrder_GenreView");
    }

    [Fact]
    public void Settings_WriteTheEncodingsEarlierVersionsRead()
    {
        var values = new Dictionary<string, object>();
        var settings = TestSettings.Create(values);

        settings.Set(AppSettings.Theme, AppTheme.Dark);
        settings.Set(AppSettings.SongListColumns, SongListColumns.Title | SongListColumns.Duration);
        settings.Set(AppSettings.LastWindowSize, (800, 600));
        settings.Set(AppSettings.LibrarySortOrder, SongSortOrder.ArtistAsc);
        settings.Set(AppSettings.PlayerTintIntensity, 2.0);

        values["AppTheme"].Should().Be("Dark");
        values["SongListColumns"].Should().Be((int)(SongListColumns.Title | SongListColumns.Duration));
        values["LastWindowSize"].Should().Be("[800,600]");
        values["SortOrder_Library"].Should().Be("ArtistAsc");
        values["PlayerTintIntensity"].Should().Be(1.0);
        settings.Get(AppSettings.LastWindowSize).Should().Be((800, 600));
    }

    [Theory]
    [InlineData("#8021944B", 128)]
    [InlineData("8021944B", 128)]
    [InlineData("#21944b", 255)]
    [InlineData("21944b", 255)]
    public void AccentColor_LoadsLegacyHexAndRoundTripsAlpha(string hex, byte alpha)
    {
        var values = new Dictionary<string, object> { ["AccentColor"] = hex };
        var settings = TestSettings.Create(values);
        var expected = new ArgbColor(alpha, 0x21, 0x94, 0x4B);

        settings.Get(AppSettings.AccentColor).Should().Be(expected);
        settings.Set(AppSettings.AccentColor, expected);
        settings.Get(AppSettings.AccentColor).Should().Be(expected);
        settings.Set(AppSettings.AccentColor, null);
        settings.Get(AppSettings.AccentColor).Should().BeNull();
        values.Should().NotContainKey("AccentColor");
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("#GG21944B")]
    public void AccentColor_InvalidSavedHexUsesDefault(string hex)
    {
        var settings = TestSettings.Create(new Dictionary<string, object> { ["AccentColor"] = hex });

        settings.Get(AppSettings.AccentColor).Should().BeNull();
    }

    [Fact]
    public void Navigation_UsesCanonicalTagsAndCurrentLabelsWhilePreservingOrderAndVisibility()
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
        var settings = TestSettings.Create(values);

        var items = settings.Get(AppSettings.NavigationItems);

        items.Select(item => item.Tag).Should().Equal("Albums", "Library", "Folders", "Playlists", "Artists", "Genres", "Insights", "Favorites");
        items.Single(item => item.Tag == "Favorites").IsEnabled.Should().BeFalse();
        items[0].IsEnabled.Should().BeFalse();
        items.Should().OnlyContain(item => item.DisplayName != "Old" && item.Icon != AppIcon.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayerButtons_PreserveSavedLayoutAndMigrateMissingDivider(bool hasDivider)
    {
        var saved = new List<PlayerButtonSetting>
        {
            new() { Id = "shuffle", IsEnabled = false, DisplayName = "Old" },
            new() { Id = "Shuffle", IsEnabled = true, DisplayName = "Old" },
            new() { Id = "Next", IsEnabled = true, DisplayName = "Old" },
            new() { Id = "Removed", DisplayName = "Old" }
        };
        if (hasDivider) saved.Insert(0, new PlayerButtonSetting { Id = "Separator", IsEnabled = false, DisplayName = "Old" });
        var settings = TestSettings.Create(new Dictionary<string, object>
        { ["PlayerButtonSettings"] = JsonSerializer.Serialize(saved) });

        var buttons = settings.Get(AppSettings.PlayerButtons);

        buttons.Select(button => button.Id).Should().Equal(hasDivider
            ? ["Separator", "Shuffle", "Next"] : ["Shuffle", "Next", "Separator"]);
        buttons.Single(button => button.Id == "Shuffle").IsEnabled.Should().BeFalse();
        buttons.Single(button => button.Id == "Separator").IsEnabled.Should().Be(!hasDivider);
        buttons.Should().OnlyContain(button => button.DisplayName != "Old");
    }

    [Fact]
    public void PaneState_ReadsLegacyStringsAndWritesNativeBooleans()
    {
        var values = new Dictionary<string, object> { ["LastPaneOpen"] = "false" };
        var settings = TestSettings.Create(values);

        settings.Get(AppSettings.LastPaneOpen).Should().BeFalse();
        settings.Set(AppSettings.LastPaneOpen, true);
        values["LastPaneOpen"].Should().BeOfType<bool>().Which.Should().BeTrue();
        settings.Get(AppSettings.LastPaneOpen).Should().BeTrue();
        values.Clear();
        settings.Get(AppSettings.LastPaneOpen).Should().BeNull();
    }
}
