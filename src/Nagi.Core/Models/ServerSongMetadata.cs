using System.Security.Cryptography;
using System.Text.Json;

namespace Nagi.Core.Models;

/// <summary>
///     A song as a music server describes it, imported the same way as tags read from a file.
///     <see cref="SongFileMetadata.FilePath" /> is assigned on import.
/// </summary>
public sealed class ServerSongMetadata : SongFileMetadata
{
    public required string RemoteId { get; init; }

    /// <summary>
    ///     The extension of the song's file on the server, such as "flac", or null when the server doesn't say.
    /// </summary>
    public string? FileExtension { get; init; }

    /// <summary>
    ///     The server's ID for the song's cover image, usually shared by every song on the album.
    /// </summary>
    public string? CoverArtId { get; init; }

    public bool IsFavorite { get; init; }

    public int PlayCount { get; init; }

    public DateTime? LastPlayedUtc { get; init; }

    public DateTime? AddedUtc { get; init; }

    /// <summary>
    ///     A hash that changes whenever anything the server said about the song changes.
    /// </summary>
    public string ComputeHash() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}
