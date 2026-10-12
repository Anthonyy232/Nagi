using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Tests;

/// <summary>
///     Lets a single substitute stand in for every library contract, for view models that
///     take several of them.
/// </summary>
public interface ILibraryFake : ILibraryReader, ILibraryWriter, ILibraryScanner, IPlaylistService
{
}
