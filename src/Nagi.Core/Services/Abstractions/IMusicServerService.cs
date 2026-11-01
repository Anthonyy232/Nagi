using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     The user's music servers: adding and syncing them, streaming their songs, and reporting plays. Each server is
///     a root folder in the library, identified by the folder's ID.
/// </summary>
public interface IMusicServerService
{
    /// <summary>
    ///     Raised on a background thread with the server's folder ID when <see cref="SyncAllAsync" /> finds that a server
    ///     no longer accepts the saved sign-in.
    /// </summary>
    event Action<Guid>? SignInRejected;

    /// <summary>
    ///     Signs in with a user name and password and adds the server to the library without syncing it.
    ///     An address without a scheme means HTTPS.
    /// </summary>
    /// <returns>The server's root folder.</returns>
    /// <exception cref="MusicServerException">The address is invalid, or the server couldn't be signed in to, isn't supported, or is already added.</exception>
    Task<Folder> AddAsync(MusicServerKind kind, string address, string userName, string password);

    /// <summary>
    ///     Adds an OpenSubsonic server with an API key, which the server must support.
    /// </summary>
    /// <inheritdoc cref="AddAsync" />
    Task<Folder> AddWithApiKeyAsync(string address, string apiKey);

    /// <summary>
    ///     Signs in to an added server again, as the same user and the same way, when it stops accepting the saved
    ///     sign-in.
    /// </summary>
    /// <param name="password">The user's password, or a new API key for a server added with one.</param>
    /// <exception cref="MusicServerException">
    ///     The server couldn't be signed in to, the API key is another account's, or the server is no longer in the library.
    /// </exception>
    Task SignInAsync(Guid folderId, string password);

    /// <summary>
    ///     Removes the server and its songs from the library, then signs out of the server in the background.
    /// </summary>
    /// <returns>False if the folder couldn't be removed, in which case Nagi stays signed in.</returns>
    Task<bool> RemoveAsync(Guid folderId);

    /// <returns>The server with its root folder, or null if the folder isn't a server.</returns>
    Task<MusicServer?> GetServerAsync(Guid folderId);

    /// <summary>
    ///     Tells the server about favorites and plays from Nagi, then makes the library match it. A sync of a server
    ///     that is already syncing waits for that sync to finish first.
    /// </summary>
    /// <exception cref="MusicServerException">The server couldn't be reached or rejected the saved sign-in.</exception>
    /// <exception cref="OperationCanceledException">Cancelled, or the server was removed while it synced.</exception>
    Task SyncAsync(Guid folderId, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Syncs every server in turn, logging the ones that fail.
    /// </summary>
    Task SyncAllAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Syncs every server in the background now and then every half hour until the app closes, so changes made on
    ///     a server reach the library while Nagi runs.
    /// </summary>
    void StartPeriodicSync();

    /// <summary>
    ///     Reports the server's plays that reached the scrobble threshold and haven't been reported, oldest first.
    ///     Plays that can't be reported stay queued.
    /// </summary>
    Task ReportPlaysAsync(Guid folderId, CancellationToken cancellationToken = default);

    /// <returns>
    ///     A URL that streams the song, or null for a song on disk or when its server is gone or has no saved sign-in.
    /// </returns>
    Task<Uri?> GetStreamUriAsync(Song song);

    /// <summary>
    ///     Finds out why a server song couldn't be played. Returning means the server looks fine, so the cause lies
    ///     elsewhere.
    /// </summary>
    /// <exception cref="MusicServerException">
    ///     The server can't be reached or rejects the saved sign-in, or the song or its server is gone
    ///     (<see cref="MusicServerError.NotFound" />).
    /// </exception>
    Task CheckSongAsync(Song song, CancellationToken cancellationToken = default);

    /// <returns>
    ///     The song's lyrics from its server, as LRC or plain text; null for a song on disk, or when the server has none
    ///     or doesn't serve lyrics.
    /// </returns>
    /// <exception cref="MusicServerException">The server can't be reached right now or rejects the saved sign-in.</exception>
    Task<string?> GetLyricsAsync(Song song, CancellationToken cancellationToken = default);
}
