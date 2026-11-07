using Nagi.Core.Library;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     The library side of the music servers that <see cref="IMusicServerService" /> manages: each server's root folder,
///     and the songs, playlists and covers synced into it. Only <see cref="IMusicServerService" /> calls it, after the
///     sign-in is saved and with one sync per server at a time.
/// </summary>
public interface IServerLibrarySync
{
    /// <summary>
    ///     Adds a music server to the library as a root folder named <paramref name="name" />, without syncing it.
    /// </summary>
    Task<Folder> AddAsync(MusicServer server, string name);

    /// <summary>
    ///     Removes the server's root folder with its songs and playlists.
    /// </summary>
    /// <returns>False if the folder isn't a server in the library or couldn't be removed.</returns>
    Task<bool> RemoveAsync(Guid folderId);

    /// <summary>
    ///     Makes a music server's root folder match the server: adds or updates every song it lists, removes the
    ///     rest, brings its playlists in line, then downloads covers that are missing or changed.
    /// </summary>
    /// <exception cref="MusicServerException">The server couldn't be read.</exception>
    /// <exception cref="OperationCanceledException">Cancelled, or the server's folder was removed while it synced.</exception>
    Task SyncAsync(Guid folderId, IMusicServerClient client, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
