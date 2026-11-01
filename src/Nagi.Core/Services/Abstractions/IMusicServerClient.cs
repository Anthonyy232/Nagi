using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     A signed-in connection to one music server. Failures throw <see cref="MusicServerException" />.
/// </summary>
public interface IMusicServerClient
{
    /// <summary>
    ///     Every song the account can play, a page at a time.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<ServerSongMetadata>> GetSongsAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     The playlists the account can see, with their songs.
    /// </summary>
    Task<IReadOnlyList<ServerPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken);

    /// <returns>The image, or null when the server has no image with that ID.</returns>
    Task<byte[]?> GetCoverArtAsync(string coverArtId, CancellationToken cancellationToken);

    /// <summary>
    ///     A URL that streams the original file. It carries the credentials itself because the player can't send
    ///     headers, so it must not be logged.
    /// </summary>
    Uri GetStreamUri(string songId);

    /// <summary>
    ///     Confirms the server can be reached, accepts the sign-in and still has the song.
    /// </summary>
    Task CheckSongAsync(string songId, CancellationToken cancellationToken);

    /// <returns>The song's lyrics as LRC, or as plain text when they have no timings; null when the song has none.</returns>
    Task<string?> GetLyricsAsync(string songId, CancellationToken cancellationToken);

    Task ReportPlayedAsync(string songId, DateTime playedUtc, CancellationToken cancellationToken);

    Task SetFavoriteAsync(IReadOnlyCollection<string> songIds, bool isFavorite, CancellationToken cancellationToken);

    Task SignOutAsync(CancellationToken cancellationToken);
}
