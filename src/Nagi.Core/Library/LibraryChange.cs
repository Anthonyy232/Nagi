using Nagi.Core.Artwork;

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

/// <summary>
///     A change to the library. Subscribers ask what it touched rather than list kinds, so a new kind only needs
///     adding here.
/// </summary>
/// <param name="FolderId">The root folder that changed, for the kinds about one root.</param>
public sealed record LibraryChange(LibraryChangeKind Kind, Guid? FolderId = null)
{
    /// <summary>Root folders were added or removed.</summary>
    public bool AffectsRoots => Kind is LibraryChangeKind.RootAdded or LibraryChangeKind.RootRemoved or LibraryChangeKind.LibraryCleared;

    /// <summary>Songs may have been added or removed.</summary>
    public bool AddsOrRemovesSongs => Kind is LibraryChangeKind.RootRemoved or LibraryChangeKind.FolderRescanned
        or LibraryChangeKind.LibraryRescanned or LibraryChangeKind.LibraryCleared;

    /// <summary>Songs, or the albums, artists and genres made from them, may have been added, removed or changed.</summary>
    public bool AffectsSongs => AddsOrRemovesSongs || Kind is LibraryChangeKind.SongsChanged;

    /// <summary>The folders, or the songs in them, may have changed.</summary>
    public bool AffectsFolders => AffectsRoots || AddsOrRemovesSongs;

    public bool AffectsFavorites => Kind is LibraryChangeKind.FavoritesChanged;
}

/// <param name="ImagePath">The artist's new cached image, or null when it has none.</param>
/// <param name="ImageModifiedUtc">When the image was saved, which <see cref="ImageUri" /> carries.</param>
public sealed record ArtistImageChange(Guid ArtistId, string? ImagePath, DateTime? ImageModifiedUtc)
{
    public ArtistImageChange(Artist artist) : this(artist.Id, artist.LocalImageCachePath, artist.ImageModifiedUtc)
    {
    }

    /// <summary>The artist's new image for display, or null when it has none.</summary>
    public string? ImageUri => ArtworkUri.Versioned(ImagePath, ImageModifiedUtc);
}
