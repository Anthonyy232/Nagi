namespace Nagi.Core.Library;

public interface ILibraryReset
{
    /// <summary>
    ///     Removes every folder, server, song, playlist and listen from the library, with the cached images and lyrics,
    ///     and forgets the servers' sign-ins. Scans and syncs still running are cancelled.
    /// </summary>
    Task ClearAllLibraryDataAsync();
}
