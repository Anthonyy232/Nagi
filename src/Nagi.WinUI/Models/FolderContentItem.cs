using Nagi.Core.Models;

namespace Nagi.WinUI.Models;

/// <summary>
///     Represents an item in a folder's content list, which can be either a subfolder or a song file.
///     This allows the folder view to display both folders and songs in a unified list.
/// </summary>
public class FolderContentItem
{
    /// <summary>
    ///     Gets or sets the type of content item.
    /// </summary>
    public FolderContentType ContentType { get; set; }

    /// <summary>
    ///     Gets or sets the folder data if this item represents a subfolder.
    /// </summary>
    public Folder? Folder { get; set; }

    /// <summary>
    ///     Gets or sets the song data if this item represents a song file.
    /// </summary>
    public Song? Song { get; set; }

    /// <summary>
    ///     Gets the display name for this item (folder name or song title).
    /// </summary>
    public string DisplayName =>
        ContentType == FolderContentType.Folder
            ? Folder?.Name ?? string.Empty
            : Song?.Title ?? string.Empty;

    /// <summary>
    ///     Gets a value indicating whether this item is a folder.
    /// </summary>
    public bool IsFolder => ContentType == FolderContentType.Folder;

    /// <summary>
    ///     Gets a value indicating whether this item is a song.
    /// </summary>
    public bool IsSong => ContentType == FolderContentType.Song;

    /// <summary>
    ///     Creates a folder content item from a Folder object.
    /// </summary>
    public static FolderContentItem FromFolder(Folder folder)
    {
        return new FolderContentItem
        {
            ContentType = FolderContentType.Folder,
            Folder = folder,
            Song = null
        };
    }

    /// <summary>
    ///     Creates a folder content item from a Song object.
    /// </summary>
    public static FolderContentItem FromSong(Song song)
    {
        return new FolderContentItem
        {
            ContentType = FolderContentType.Song,
            Folder = null,
            Song = song
        };
    }
}

/// <summary>
///     Defines the type of content item in a folder view.
/// </summary>
public enum FolderContentType
{
    Folder,
    Song
}