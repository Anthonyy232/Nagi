using System.Text.Json;
using Nagi.Core.Library;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Player;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Services;

/// <summary>
///     The settings of the app's interface and windows.
/// </summary>
public static class AppSettings
{
    public static readonly Setting<AppTheme> Theme = Setting.EnumName("AppTheme", AppTheme.Default);
    public static readonly Setting<BackdropMaterial> BackdropMaterial = Setting.EnumName("BackdropMaterial", Models.BackdropMaterial.Mica);
    public static readonly Setting<bool> DynamicTheming = Setting.Value("DynamicThemingEnabled", true);

    /// <summary>Null for Nagi's brand color.</summary>
    public static readonly Setting<ArgbColor?> AccentColor = new("AccentColor", () => null,
        stored => stored is string hex && ArgbColor.TryParseHex(hex, out var color) ? color : null,
        color => color?.ToHex());

    public static readonly Setting<PlayerBackgroundMaterial> PlayerBackgroundMaterial =
        Setting.EnumName("PlayerBackgroundMaterial", Models.PlayerBackgroundMaterial.Acrylic);

    /// <summary>From 0 (neutral) to 1 (vibrant).</summary>
    public static readonly Setting<double> PlayerTintIntensity =
        Setting.Value("PlayerTintIntensity", 1.0, intensity => Math.Clamp(intensity, 0.0, 1.0));

    /// <summary>A BCP-47 language tag, or empty to follow Windows.</summary>
    public static readonly Setting<string> Language = Setting.Value("AppLanguage", string.Empty);

    public static readonly Setting<bool> PlayerAnimation = Setting.Value("PlayerAnimationEnabled", true);
    public static readonly Setting<bool> StartMinimized = Setting.Value("StartMinimizedEnabled", false);
    public static readonly Setting<bool> HideToTray = Setting.Value("HideToTrayEnabled", true);
    public static readonly Setting<bool> MinimizeToMiniPlayer = Setting.Value("MinimizeToMiniPlayerEnabled", false);
    public static readonly Setting<bool> ShowCoverArtInTrayFlyout = Setting.Value("ShowCoverArtInTrayFlyout", true);

    /// <summary>Every destination in menu order, shown or not.</summary>
    public static readonly Setting<List<NavigationItemSetting>> NavigationItems =
        Setting.Json("NavigationItems", DefaultNavigationItems, saved =>
        {
            var defaults = DefaultNavigationItems();
            return saved.DistinctBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
                .Join(defaults, item => item.Tag, item => item.Tag, (configuration, definition) =>
                {
                    definition.IsEnabled = configuration.IsEnabled;
                    return definition;
                }, StringComparer.OrdinalIgnoreCase)
                .Concat(defaults)
                .DistinctBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

    /// <summary>Every player button in order, shown or not, with a separator between playback and the rest.</summary>
    public static readonly Setting<List<PlayerButtonSetting>> PlayerButtons =
        Setting.Json("PlayerButtonSettings", DefaultPlayerButtons, saved =>
        {
            var defaults = DefaultPlayerButtons();
            var buttons = saved.DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Join(defaults, item => item.Id, item => item.Id, (configuration, definition) =>
                {
                    definition.IsEnabled = configuration.IsEnabled;
                    return definition;
                }, StringComparer.OrdinalIgnoreCase).ToList();

            // Older saved layouts predate the divider between playback and auxiliary buttons.
            var separatorIndex = defaults.FindIndex(item => item.IsSeparator);
            if (!buttons.Any(item => item.IsSeparator))
                buttons.Insert(Math.Min(separatorIndex, buttons.Count), defaults[separatorIndex]);
            return buttons;
        });

    public static readonly Setting<QueueDisplaySettings> QueueDisplay =
        Setting.Json("QueueDisplaySettings", () => new QueueDisplaySettings());

    public static readonly Setting<SongListColumns> SongListColumns = new("SongListColumns", DefaultSongListColumns,
        stored => stored is int columns ? (SongListColumns)columns : DefaultSongListColumns(),
        columns => (int)columns);

    public static readonly Setting<bool> ShowSongListHeader = Setting.Value("ShowSongListHeader", true);

    /// <summary>0 shows every song on one page.</summary>
    public static readonly Setting<int> SongsPerPage = Setting.Value("SongsPerPage", 50);

    /// <summary>The values <see cref="SongsPerPage" /> offers, smallest first.</summary>
    public static IReadOnlyList<int> PageSizeOptions { get; } = [25, 50, 100, 250, 500, 0];

    public static readonly Setting<bool> RememberWindowSize = Setting.Value("RememberWindowSizeEnabled", false);

    /// <summary>In pixels. Null until a size is saved.</summary>
    public static readonly Setting<(int Width, int Height)?> LastWindowSize = IntPair("LastWindowSize");

    public static readonly Setting<bool> RememberWindowPosition = Setting.Value("RememberWindowPositionEnabled", false);

    /// <summary>In pixels. Null until a position is saved.</summary>
    public static readonly Setting<(int X, int Y)?> LastWindowPosition = IntPair("LastWindowPosition");

    public static readonly Setting<bool> RememberPaneState = Setting.Value("RememberPaneStateEnabled", true);

    /// <summary>Null until the pane state is saved.</summary>
    public static readonly Setting<bool?> LastPaneOpen = new("LastPaneOpen", () => null, stored => stored switch
    {
        bool isOpen => isOpen,
        // Older versions saved the state as text.
        string legacy when bool.TryParse(legacy, out var isOpen) => isOpen,
        _ => null
    }, isOpen => isOpen);

    public static readonly Setting<SongSortOrder> LibrarySortOrder = Setting.EnumName("SortOrder_Library", SongSortOrder.TitleAsc);
    public static readonly Setting<AlbumSortOrder> AlbumsSortOrder = Setting.EnumName("SortOrder_Albums", AlbumSortOrder.ArtistAsc);
    public static readonly Setting<ArtistSortOrder> ArtistsSortOrder = Setting.EnumName("SortOrder_Artists", ArtistSortOrder.NameAsc);
    public static readonly Setting<GenreSortOrder> GenresSortOrder = Setting.EnumName("SortOrder_Genres", GenreSortOrder.NameAsc);
    public static readonly Setting<PlaylistSortOrder> PlaylistsSortOrder = Setting.EnumName("SortOrder_Playlists", PlaylistSortOrder.NameAsc);
    public static readonly Setting<SongSortOrder> FolderViewSortOrder = Setting.EnumName("SortOrder_FolderView", SongSortOrder.TitleAsc);
    public static readonly Setting<SongSortOrder> AlbumViewSortOrder = Setting.EnumName("SortOrder_AlbumView", SongSortOrder.TrackNumberAsc);
    public static readonly Setting<SongSortOrder> ArtistViewSortOrder = Setting.EnumName("SortOrder_ArtistView", SongSortOrder.AlbumAsc);
    public static readonly Setting<SongSortOrder> GenreViewSortOrder = Setting.EnumName("SortOrder_GenreView", SongSortOrder.TitleAsc);

    private static SongListColumns DefaultSongListColumns() =>
        Lists.SongListColumns.Title | Lists.SongListColumns.Artist | Lists.SongListColumns.Album |
        Lists.SongListColumns.Year | Lists.SongListColumns.Duration;

    // Stored as a JSON array of two numbers.
    private static Setting<(int, int)?> IntPair(string key) => new(key, () => null,
        stored => stored is string json && JsonSerializer.Deserialize<int[]>(json) is [var first, var second]
            ? (first, second)
            : null,
        pair => pair is { } value ? JsonSerializer.Serialize(new[] { value.Item1, value.Item2 }) : null);

    private static List<NavigationItemSetting> DefaultNavigationItems() =>
    [
        new() { DisplayName = Strings.Settings_Nav_Library, Tag = NavigationTags.Library, Icon = AppIcon.Library, IsEnabled = true },
        new() { DisplayName = Strings.Settings_Nav_Folders, Tag = NavigationTags.Folders, Icon = AppIcon.Folders, IsEnabled = true },
        new() { DisplayName = Strings.Settings_Nav_Playlists, Tag = NavigationTags.Playlists, Icon = AppIcon.Playlists, IsEnabled = true },
        new() { DisplayName = Strings.Settings_Nav_Artists, Tag = NavigationTags.Artists, Icon = AppIcon.Artists, IsEnabled = true },
        new() { DisplayName = Strings.Settings_Nav_Albums, Tag = NavigationTags.Albums, Icon = AppIcon.Albums, IsEnabled = true },
        new() { DisplayName = Strings.Settings_Nav_Genres, Tag = NavigationTags.Genres, Icon = AppIcon.Genres, IsEnabled = true },
        new() { DisplayName = Strings.Settings_Nav_Insights, Tag = NavigationTags.Insights, Icon = AppIcon.Insights, IsEnabled = false },
        new() { DisplayName = Strings.Listening_Favorites, Tag = NavigationTags.Favorites, Icon = AppIcon.Favorites, IsEnabled = false }
    ];

    private static List<PlayerButtonSetting> DefaultPlayerButtons() =>
    [
        new() { Id = PlayerButtonIds.Shuffle, DisplayName = Strings.Settings_Button_Shuffle, Icon = AppIcon.Shuffle, IsEnabled = true },
        new() { Id = PlayerButtonIds.Previous, DisplayName = Strings.Settings_Button_Previous, Icon = AppIcon.Previous, IsEnabled = true },
        new() { Id = PlayerButtonIds.PlayPause, DisplayName = Strings.Settings_Button_PlayPause, Icon = AppIcon.Play, IsEnabled = true },
        new() { Id = PlayerButtonIds.Next, DisplayName = Strings.Settings_Button_Next, Icon = AppIcon.Next, IsEnabled = true },
        new() { Id = PlayerButtonIds.Repeat, DisplayName = Strings.Settings_Button_Repeat, Icon = AppIcon.RepeatAll, IsEnabled = true },
        new() { Id = PlayerButtonIds.Separator, DisplayName = Strings.Settings_Button_Divider, IsEnabled = true },
        new() { Id = PlayerButtonIds.Lyrics, DisplayName = Strings.Settings_Button_Lyrics, Icon = AppIcon.Lyrics, IsEnabled = true },
        new() { Id = PlayerButtonIds.Queue, DisplayName = Strings.Settings_Button_Queue, Icon = AppIcon.Queue, IsEnabled = true },
        new() { Id = PlayerButtonIds.Volume, DisplayName = Strings.Settings_Button_Volume, Icon = AppIcon.VolumeHigh, IsEnabled = true }
    ];
}
