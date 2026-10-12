using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Tests.Utils;

/// <summary>
///     Lets a single substitute stand in for every library contract, for code under test that
///     takes several of them.
/// </summary>
public interface ILibraryFake : ILibraryReader, ILibraryWriter, ILibraryScanner, IPlaylistService
{
}
