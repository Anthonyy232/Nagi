namespace Nagi.Core.Library;

/// <summary>
///     One page of a directory: its sub-folders, then its songs. <see cref="PagedResult{T}.Items" /> holds the page's
///     songs, and <see cref="PagedResult{T}.TotalCount" /> counts the sub-folders and songs together, as the pages are cut.
/// </summary>
public sealed class DirectoryPage : PagedResult<Song>
{
    public IReadOnlyList<Folder> Folders { get; init; } = [];

    public int FolderCount { get; init; }

    public int SongCount => TotalCount - FolderCount;
}
