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
    public ILibraryWriter Writer { get; } = Substitute.For<ILibraryWriter>();
    public ILibraryScanner Scanner { get; } = Substitute.For<ILibraryScanner>();
    public IPlaylistService Playlists { get; } = Substitute.For<IPlaylistService>();

    public void ClearReceivedCalls()
    {
        Reader.ClearReceivedCalls();
        Writer.ClearReceivedCalls();
        Scanner.ClearReceivedCalls();
        Playlists.ClearReceivedCalls();
    }
}
