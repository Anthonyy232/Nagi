using System;

namespace Nagi.Core.Services.Data;

/// <summary>
///     Specifies the type of change that occurred in the library.
/// </summary>
public enum LibraryChangeType
{
    FolderAdded,

    FolderRemoved,

    FolderRescanned,

    LibraryRescanned,

    SongsUpdated
}

/// <summary>
///     Provides data for the <see cref="Services.Abstractions.ILibraryScanner.LibraryContentChanged"/> event.
/// </summary>
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
