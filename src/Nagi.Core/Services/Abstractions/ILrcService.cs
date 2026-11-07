using Nagi.Core.Models;
using Nagi.Core.Models.Lyrics;

namespace Nagi.Core.Services.Abstractions;

public interface ILrcService
{
    /// <summary>
    ///     Finds a song's lyrics: its lyrics file (a sidecar or Nagi's cache), then lyrics embedded in its tags, then
    ///     its music server's, then the enabled online providers in priority order. Online providers are asked only
    ///     while online lyrics are turned on and the song was never looked up, or its lyrics file is gone.
    /// </summary>
    /// <returns>The lyrics, or null if none were found.</returns>
    Task<SongLyrics?> GetLyricsAsync(Song song, CancellationToken cancellationToken = default);

    /// <summary>Saves a lyrics override in Nagi's cache.</summary>
    Task SaveLyricsAsync(Song song, string lrcContent);

    /// <summary>Removes cached lyrics without deleting external sidecars.</summary>
    Task<bool> RemoveCachedLyricsAsync(Song song);

    /// <summary>Whether Nagi manages the song's lyrics file.</summary>
    bool HasCachedLyrics(Song song);
}
