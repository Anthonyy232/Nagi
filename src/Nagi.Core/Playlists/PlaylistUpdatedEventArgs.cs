using Nagi.Core.Artwork;

namespace Nagi.Core.Playlists;

public class PlaylistUpdatedEventArgs : EventArgs
{
    public Guid PlaylistId { get; }
    public string? CoverImageUri { get; }
    public DateTime DateModified { get; }

    /// <summary>The cover for display, or null when the playlist has none.</summary>
    public string? CoverUri => ArtworkUri.Versioned(CoverImageUri, DateModified);

    public PlaylistUpdatedEventArgs(Guid playlistId, string? coverImageUri, DateTime dateModified)
    {
        PlaylistId = playlistId;
        CoverImageUri = coverImageUri;
        DateModified = dateModified;
    }
}
