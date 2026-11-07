using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Announces changes to the library. Raised on the thread that made the change, after it was saved.
/// </summary>
public interface ILibraryNotifier
{
    event EventHandler<LibraryChange>? Changed;

    event EventHandler<IReadOnlyList<ArtistImageChange>>? ArtistImagesChanged;
}
