namespace Nagi.Core.Library;

/// <summary>
///     Announces changes to the library. Raised on the thread that made the change, after it was saved.
/// </summary>
public interface ILibraryNotifier
{
    event EventHandler<LibraryChange>? Changed;

    event EventHandler<IReadOnlyList<ArtistImageChange>>? ArtistImagesChanged;
}
