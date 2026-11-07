using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public sealed class LibraryNotifier : ILibraryNotifier
{
    public event EventHandler<LibraryChange>? Changed;

    public event EventHandler<IReadOnlyList<ArtistImageChange>>? ArtistImagesChanged;

    internal void Raise(LibraryChangeKind kind, Guid? folderId = null) => Changed?.Invoke(this, new LibraryChange(kind, folderId));

    internal void RaiseArtistImagesChanged(IReadOnlyList<ArtistImageChange> changes)
    {
        if (changes.Count > 0) ArtistImagesChanged?.Invoke(this, changes);
    }
}
