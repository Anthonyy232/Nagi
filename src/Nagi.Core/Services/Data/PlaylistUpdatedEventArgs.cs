namespace Nagi.Core.Services.Data;

public class PlaylistUpdatedEventArgs : EventArgs
{
    public Guid PlaylistId { get; }
    public string? CoverImageUri { get; }
    public DateTime? DateModified { get; }

    public PlaylistUpdatedEventArgs(Guid playlistId, string? coverImageUri, DateTime? dateModified = null)
    {
        PlaylistId = playlistId;
        CoverImageUri = coverImageUri;
        DateModified = dateModified;
    }
}
