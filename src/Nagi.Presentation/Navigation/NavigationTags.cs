namespace Nagi.Presentation.Navigation;

/// <summary>
///     The destinations of the navigation menu. The user's menu layout is saved by tag, so they never change.
/// </summary>
public static class NavigationTags
{
    public const string Library = "Library";
    public const string Folders = "Folders";
    public const string Playlists = "Playlists";
    public const string Artists = "Artists";
    public const string Albums = "Albums";
    public const string Genres = "Genres";
    public const string Insights = "Insights";

    /// <summary>The library page showing only favorites; also the parameter that opens it that way.</summary>
    public const string Favorites = "Favorites";
}
