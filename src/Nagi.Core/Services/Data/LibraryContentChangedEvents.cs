using System;

namespace Nagi.Core.Services.Data;

public enum LibraryChangeType
{
    FolderAdded,

    FolderRemoved,

    FolderRescanned,

    LibraryRescanned,

    SongsUpdated
}

public class LibraryContentChangedEventArgs : EventArgs
{
    public LibraryChangeType ChangeType { get; }

    /// <summary>
    ///     The ID of the folder involved in the change, if applicable (e.g. Added, Removed, Rescanned).
    ///     Will be null for full library refreshes.
    /// </summary>
    public Guid? FolderId { get; }

    public LibraryContentChangedEventArgs(LibraryChangeType changeType, Guid? folderId = null)
    {
        ChangeType = changeType;
        FolderId = folderId;
    }
}
