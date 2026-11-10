using System.Collections.Generic;
using System.Linq;
using Nagi.Presentation.Navigation;
using Nagi.WinUI.Pages;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     The pages behind the navigation menu's tags, and the pages opened from them.
/// </summary>
internal static class AppDestinations
{
    /// <summary>The tag of the navigation view's own settings item, which the user's menu layout leaves out.</summary>
    public const string SettingsTag = "Settings";

    private static readonly Dictionary<string, (Type Page, object? Parameter)> MenuPages = new()
    {
        [NavigationTags.Library] = (typeof(LibraryPage), null),
        [NavigationTags.Favorites] = (typeof(LibraryPage), NavigationTags.Favorites),
        [NavigationTags.Folders] = (typeof(FolderPage), null),
        [NavigationTags.Playlists] = (typeof(PlaylistPage), null),
        [NavigationTags.Artists] = (typeof(ArtistPage), null),
        [NavigationTags.Albums] = (typeof(AlbumPage), null),
        [NavigationTags.Genres] = (typeof(GenrePage), null),
        [NavigationTags.Insights] = (typeof(InsightsPage), null),
        [SettingsTag] = (typeof(SettingsPage), null)
    };

    // Each detail page highlights the menu item it was opened from.
    private static readonly (Type Parameter, Type Page, string MenuTag)[] DetailPages =
    [
        (typeof(AlbumViewNavigationParameter), typeof(AlbumViewPage), NavigationTags.Albums),
        (typeof(ArtistViewNavigationParameter), typeof(ArtistViewPage), NavigationTags.Artists),
        (typeof(FolderSongViewNavigationParameter), typeof(FolderSongViewPage), NavigationTags.Folders),
        (typeof(GenreViewNavigationParameter), typeof(GenreViewPage), NavigationTags.Genres),
        (typeof(PlaylistSongViewNavigationParameter), typeof(PlaylistSongViewPage), NavigationTags.Playlists),
        (typeof(SmartPlaylistSongViewNavigationParameter), typeof(SmartPlaylistSongViewPage), NavigationTags.Playlists)
    ];

    public static bool TryGetMenuPage(string tag, out Type page, out object? parameter)
    {
        var found = MenuPages.TryGetValue(tag, out var destination);
        (page, parameter) = destination;
        return found;
    }

    /// <summary>
    ///     The menu tag to highlight for <paramref name="page" /> opened with <paramref name="parameter" />, or null when
    ///     no menu item leads there.
    /// </summary>
    public static string? GetMenuTag(Type? page, object? parameter) =>
        MenuPages.FirstOrDefault(entry => entry.Value.Page == page && Equals(entry.Value.Parameter, parameter)).Key
        ?? DetailPages.FirstOrDefault(entry => entry.Page == page).MenuTag;

    /// <summary>Whether <paramref name="page" /> is opened over a menu page, so the back button returns there.</summary>
    public static bool ShowsBackButton(Type page) =>
        page == typeof(LyricsPage) || DetailPages.Any(entry => entry.Page == page);

    public static Type GetDetailPage(INavigationParameter parameter) =>
        DetailPages.FirstOrDefault(entry => entry.Parameter == parameter.GetType()).Page
        ?? throw new ArgumentException($"No page accepts {parameter.GetType().Name}.", nameof(parameter));
}
