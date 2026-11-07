using Nagi.Core.Library;
using Nagi.Core.Services.Abstractions;
using NSubstitute;

namespace Nagi.Core.Tests.Utils;

/// <summary>
///     One independent substitute per library contract, so a test only satisfies the calls
///     the code under test makes through the interface it was actually given.
/// </summary>
public sealed class LibraryFakes
{
    public ILibraryReader Reader { get; } = Substitute.For<ILibraryReader>();
    public ILibraryNotifier Notifier { get; } = Substitute.For<ILibraryNotifier>();
    public ILibraryFolderService Folders { get; } = Substitute.For<ILibraryFolderService>();
    public IListeningService Listening { get; } = Substitute.For<IListeningService>();
    public IArtworkService Artwork { get; } = Substitute.For<IArtworkService>();
    public IPlaylistService Playlists { get; } = Substitute.For<IPlaylistService>();

    public void ClearReceivedCalls()
    {
        Reader.ClearReceivedCalls();
        Folders.ClearReceivedCalls();
        Listening.ClearReceivedCalls();
        Artwork.ClearReceivedCalls();
        Playlists.ClearReceivedCalls();
    }
}
