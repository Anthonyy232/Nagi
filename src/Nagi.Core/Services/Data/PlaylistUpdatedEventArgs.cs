using System;

namespace Nagi.Core.Services.Data;

public class PlaylistUpdatedEventArgs : EventArgs
{
    public Guid PlaylistId { get; }
    public string? CoverImageUri { get; }

    public PlaylistUpdatedEventArgs(Guid playlistId, string? coverImageUri)
    {
        PlaylistId = playlistId;
        CoverImageUri = coverImageUri;
    }
}
