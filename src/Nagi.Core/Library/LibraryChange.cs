namespace Nagi.Core.Library;

public enum LibraryChangeKind
{
    RootAdded,
    RootRemoved,

    /// <summary>A scan or sync added, changed or removed songs in one root folder.</summary>
    FolderRescanned,

    /// <summary>Songs in several root folders changed at once.</summary>
    LibraryRescanned,

    /// <summary>Everything was removed from the library.</summary>
    LibraryCleared,

    /// <summary>Song details such as covers changed without a scan.</summary>
    SongsChanged,

    FavoritesChanged,
    ListenHistoryCleared
}

/// <param name="FolderId">The root folder that changed, for the kinds about one root.</param>
public sealed record LibraryChange(LibraryChangeKind Kind, Guid? FolderId = null);

/// <param name="ImagePath">The artist's new cached image, or null when it has none.</param>
public sealed record ArtistImageChange(Guid ArtistId, string? ImagePath);
