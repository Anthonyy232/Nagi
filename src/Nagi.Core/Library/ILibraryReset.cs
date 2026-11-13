namespace Nagi.Core.Library;

public interface ILibraryReset
{
    /// <summary>
    ///     Removes every folder, server, song, playlist and listen from the library, with the cached images and lyrics,
    ///     and forgets the servers' sign-ins. Scans and syncs still running are cancelled.
    /// </summary>
    Task ClearAllLibraryDataAsync();

    /// <summary>
    ///     Runs <paramref name="action" /> while nothing writes to the library database or its caches: the scan the user
    ///     started and the server syncs are cancelled, other writers and the online artwork fetch are waited for, and
    ///     none starts until the action finishes. <paramref name="reason" /> is logged with the cancelled scan.
    /// </summary>
    Task RunWhileQuietAsync(string reason, Func<Task> action);
}
