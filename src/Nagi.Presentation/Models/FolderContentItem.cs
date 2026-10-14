using System;
using Nagi.Core.Models;

namespace Nagi.Presentation.Models;

public sealed class FolderContentItem
{
    private FolderContentItem(Folder folder) => Folder = folder;
    private FolderContentItem(Song song) => Song = song;

    public Folder? Folder { get; }
    public Song? Song { get; }
    public bool IsFolder => Folder is not null;
    public bool IsSong => Song is not null;
    public string DisplayName => Folder?.Name ?? Song!.Title;
    public Guid Id => Folder?.Id ?? Song!.Id;

    public static FolderContentItem FromFolder(Folder folder) => new(folder);
    public static FolderContentItem FromSong(Song song) => new(song);
}
