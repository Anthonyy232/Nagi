namespace Nagi.Services.Abstractions;

/// <summary>
/// A composite service interface that aggregates all library-related contracts.
/// This is primarily for convenience in service registration.
/// For dependency injection, prefer depending on more granular interfaces like <see cref="ILibraryReader"/>.
/// </summary>
public interface ILibraryService : ILibraryReader, ILibraryWriter, ILibraryScanner, IPlaylistService {
}