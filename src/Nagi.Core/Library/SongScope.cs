namespace Nagi.Core.Library;

/// <summary>
///     Which songs a list or a queue holds.
/// </summary>
public abstract record SongScope
{
    private SongScope()
    {
    }

    public static SongScope Library { get; } = new LibraryScope();

    public static SongScope Favorites { get; } = new FavoritesScope();

    public sealed record LibraryScope : SongScope;

    public sealed record FavoritesScope : SongScope;

    public sealed record Album(Guid AlbumId) : SongScope;

    public sealed record Artist(Guid ArtistId) : SongScope;

    public sealed record Genre(Guid GenreId) : SongScope;

    /// <summary>
    ///     A playlist's songs, which <see cref="SongSortOrder.PlaylistOrder" /> lists in the playlist's order.
    /// </summary>
    public sealed record Playlist(Guid PlaylistId) : SongScope;

    /// <summary>
    ///     Every song in a root folder.
    /// </summary>
    public sealed record Folder(Guid RootFolderId) : SongScope;

    /// <summary>
    ///     The songs directly in one directory of a root folder.
    /// </summary>
    public sealed record Directory(Guid RootFolderId, string Path) : SongScope;

    /// <summary>
    ///     The songs in one directory of a root folder and in the directories under it.
    /// </summary>
    public sealed record DirectoryTree(Guid RootFolderId, string Path) : SongScope;
}
