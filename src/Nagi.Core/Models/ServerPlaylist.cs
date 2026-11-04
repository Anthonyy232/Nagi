namespace Nagi.Core.Models;

/// <summary>
///     A playlist as a music server describes it.
/// </summary>
/// <param name="SongIds">The server's IDs for the playlist's songs, in order. A song can appear more than once.</param>
public sealed record ServerPlaylist(string RemoteId, string Name, DateTime? CreatedUtc, IReadOnlyList<string> SongIds);
